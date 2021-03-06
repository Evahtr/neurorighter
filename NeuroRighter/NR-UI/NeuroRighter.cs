// NEURORIGHTER.CS
// Copyright (c) 2008-2011 John Rolston
//
// This file is part of NeuroRighter.
//
// NeuroRighter is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// NeuroRighter is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with NeuroRighter.  If not, see <http://www.gnu.org/licenses/>.

//#define USE_LOG_FILE
//#define DEBUG

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.IO;
using System.IO.Ports;
using System.Runtime.InteropServices;
using NationalInstruments;
using NationalInstruments.DAQmx;
using NationalInstruments.UI;
using NationalInstruments.UI.WindowsForms;
using NationalInstruments.Analysis;
using NationalInstruments.Analysis.Dsp;
using NationalInstruments.Analysis.Dsp.Filters;
using NationalInstruments.Analysis.Math;
using NationalInstruments.Analysis.SignalGeneration;
using csmatio.types;
using csmatio.io;
using NeuroRighter.Aquisition;
using rawType = System.Double;
using NeuroRighter.SpikeDetection;
using NeuroRighter.FileWriting;
using ExtensionMethods;
using NeuroRighter.Server;
using NeuroRighter.DataTypes;
using NeuroRighter.Log;
using ConsoleWidget;

namespace NeuroRighter
{

    ///<summary>Main Form for NeuroRighter application.</summary>
    ///<author>John Rolston</author>
    sealed internal partial class NeuroRighter : Form
    {
        // Neurorighter object constructor
        private void tweakGui()
        {

            radioButton_stimVoltageControlled.Checked = Properties.Settings.Default.StimVoltageControlled;

            radioButton_stimCurrentControlled.Checked = !radioButton_stimVoltageControlled.Checked;
            radioButton_impVoltage.Checked = radioButton_stimVoltageControlled.Checked;
            radioButton_impCurrent.Checked = radioButton_stimCurrentControlled.Checked;
     
        }

        public NeuroRighter()
        {

            InitializeComponent();
            tweakGui();
            // Save window state in application data folder
            NRPersistWindowComponent.XMLFilePath = Properties.Settings.Default.persistWindowPath;

            //Set version number
            this.Text += System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
            this.Text += " (BETA)";

            // Set spike buffer lengths
            spikeBufferLength = Convert.ToInt32(Properties.Settings.Default.ADCPollingPeriodSec * Properties.Settings.Default.RawSampleFrequency);
            lfpBufferLength = Convert.ToInt32(Properties.Settings.Default.ADCPollingPeriodSec * Properties.Settings.Default.LFPSampleFrequency);
            eegBufferLength = Convert.ToInt32(Properties.Settings.Default.ADCPollingPeriodSec * Properties.Settings.Default.EEGSamplingRate);
            
            
            
            this.numChannels =Properties.Settings.Default.NumChannels;
            this.numChannelsPerDev = (numChannels < 32 ? numChannels : 32);
            this.currentRef = new int[2];

            // Set up console output
            nrConsole = new ConsoleControl();

            // Create a new spike detection form so we can access its parameters
            setSpikeDetectorSettings();

            // Create a raw-scaler so that we can store the doubles produced by the NI tasks as 16-bit integers
            neuralDataScaler = new RawScale();
            auxDataScaler = new RawScale();
            
            //Setup default filename and create recordingSettings object
            this.filenameOutput = "test0001";
            recordingSettings = new RecordingSetup();
            recordingSettings.SettingsHaveChanged += new RecordingSetup.resetRecordingSettingsHandler(recordingSettings_SettingsHaveChanged);
            recordingSettings.SetSpikeFiltAccess(checkBox_spikesFilter.Checked);
            recordingSettings.RecallDefaultSettings();

            
            

            // Recall default settings
            string[] deviceNames = DaqSystem.Local.Devices;
            foreach (string dname in deviceNames)
            {
                Device d = DaqSystem.Local.LoadDevice(dname);
                d.Reset();
            }

            // Update recording and stimulation settings
            updateSettings();

            //Create plots
            try
            {
                double gain = 20.0 / Properties.Settings.Default.A2Dgain;

                spikeGraph = new GridGraph();
                spikeGraph.setup(4, 4, 100, false, 1 / 4.0, gain);
                spikeGraph.Resize += new EventHandler(spikeGraph.resize);
                spikeGraph.VisibleChanged += new EventHandler(spikeGraph.resize);
                spikeGraph.Parent = tabPage_spikes;
                spikeGraph.Dock = DockStyle.Fill;

                resetSpkWfm();
            }
            catch (InvalidOperationException e)
            {
                MessageBox.Show("Your graphics card is unsupported. Recording will be disabled." + e.Message);
                buttonStart.Enabled = false;
            }

            //Load gain settings
            //Enable channel output button, if appropriate
            channelOut.Enabled = Properties.Settings.Default.UseSingleChannelPlayback;

            //Switch referencing scheme to last used
            switch (Properties.Settings.Default.SpikesReferencingScheme)
            {
                case 0:
                    radioButton_spikesReferencingCommonAverage.Checked = true;
                    break;
                case 1:
                    radioButton_spikesReferencingCommonMedian.Checked = true;
                    break;
                case 2:
                    radioButton_spikesReferencingCommonMedianLocal.Checked = true;
                    break;
                case 3:
                    radioButton_spikeReferencingNone.Checked = true;
                    break;
            }

            //Load saved filter settings
            SpikeLowCut.Value = Convert.ToDecimal(Properties.Settings.Default.SpikesLowCut);
            SpikeHighCut.Value = Convert.ToDecimal(Properties.Settings.Default.SpikesHighCut);
            SpikeFiltOrder.Value = Convert.ToDecimal(Properties.Settings.Default.SpikesNumPoles);
            LFPLowCut.Value = Convert.ToDecimal(Properties.Settings.Default.LFPLowCut);
            LFPHighCut.Value = Convert.ToDecimal(Properties.Settings.Default.LFPHighCut);
            LFPFiltOrder.Value = Convert.ToDecimal(Properties.Settings.Default.LFPNumPoles);

        }

        // Start button
        internal void buttonStart_Click(object sender, EventArgs e)
        {
            //Ensure that, if recording is setup, that it has been done properly
            //Retrain Spike detector if required
            if (checkBox_RetrainOnRestart.Checked)
                setSpikeDetector();

            // Call the recording setup/start functions
            bool recordingCancelled = true;
            if (!taskRunning)
            {
                UpdateRecordingSettings();
                recordingCancelled=NRAcquisitionSetup();
            }

            if (!recordingCancelled)
            {
                NRStartRecording();
            }
            else
            {
                buttonStop_Click(null, null);
            }

        }

        // Method to set up the recording side of neurorighter
        private bool NRAcquisitionSetup()
        {
            lock (this)
            {
                if (!taskRunning)
                {
                    try
                    {
                        
                        this.Cursor = Cursors.WaitCursor;
                       if (switch_record.Value)
                        {
                            // Create file name
                            if (filenameBase == null) //user hasn't specified a file
                                button_BrowseOutputFile_Click(null, null); //call file selection routine
                            if (filenameBase == null) //this happens if the user pressed cancel for the dialog
                            {
                                MessageBox.Show("An output file must be selected before recording."); //display an error message
                                this.Cursor = Cursors.Default;
                                return true;
                            }

                            // If the user is just doing repeated recordings
                            if (checkbox_repeatRecord.Checked || Properties.Settings.Default.useFidTimeStamp)
                            {
                                DateTime nowDate = DateTime.Now;//Get current time (local to computer);
                                string datePrefix = nowDate.ToString("'-'yyyy'-'MM'-'dd'-'HH'-'mm'-'ss");
                                filenameBase = originalNameBase + datePrefix;
                            }

                            // Look for old files with same name
                            string[] matchFiles;
                            try
                            {
                                matchFiles = Directory.GetFiles(currentSaveDir, currentSaveFile + "*");
                            }
                            catch
                            {
                                matchFiles = new string[0];
                            }

                            if (matchFiles.Length > 0)
                            {
                                DialogResult dr = MessageBox.Show("File " + filenameBase + " exists. Overwrite?",
                                    "NeuroRighter Warning", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);

                                if (dr == DialogResult.No)
                                    button_BrowseOutputFile_Click(null, null); //call file selection routine
                                else if (dr == DialogResult.Cancel)
                                {
                                    this.Cursor = Cursors.Default;
                                    return true;
                                }
                            }

                            // Set file base name + number of channels
                            recordingSettings.SetFID(filenameBase);
                            recordingSettings.SetNumElectrodes(numChannels);
                        }

                        // Find out how many devs and channels/dev we are going to need
                        int numDevices = (numChannels > 32 ? Properties.Settings.Default.AnalogInDevice.Count : 1);
                        numChannelsPerDev = (numChannels < 32 ? numChannels : 32);

                        // Set spike buffer lengths
                        spikeBufferLength = Convert.ToInt32(Properties.Settings.Default.ADCPollingPeriodSec * Properties.Settings.Default.RawSampleFrequency);
                        lfpBufferLength = Convert.ToInt32(Properties.Settings.Default.ADCPollingPeriodSec * Properties.Settings.Default.LFPSampleFrequency);

                        // Create spike aquisition task list
                        spikeTask = new List<Task>(numDevices);
                        Properties.Settings.Default.numSpikeTasks = numDevices;
                        NRAIChannelCollection spikeAqSet = new NRAIChannelCollection(numDevices, numChannelsPerDev);
                        spikeAqSet.SetupSpikeCollection(ref spikeTask);

                        // Check audio and video properties
                        if (Properties.Settings.Default.UseSingleChannelPlayback)
                            spikeOutTask = new Task("spikeOutTask"); //For audio output
                        if (checkBox_video.Checked) //NB: This can't be checked unless video is enabled (no need to check properties)
                            triggerTask = new Task("triggerTask");

                        // Set MUA sample rate
                        double muaSamplingRate = spikeSamplingRate / MUA_DOWNSAMPLE_FACTOR;

                        //Add LFP channels, if configured
                        if (Properties.Settings.Default.SeparateLFPBoard && Properties.Settings.Default.UseLFPs)
                        {
                            lfpTask = new Task("lfpTask");
                            for (int i = 0; i < Properties.Settings.Default.NumChannels; ++i)
                                lfpTask.AIChannels.CreateVoltageChannel(Properties.Settings.Default.LFPDevice + "/ai" + i.ToString(), "",
                                    AITerminalConfiguration.Nrse, -10.0, 10.0, AIVoltageUnits.Volts);
                            setGain(lfpTask, Properties.Settings.Default.LFPgain);
                            lfpTask.Control(TaskAction.Verify);
                        }


                        //Add EEG channels, if configured
                        if (Properties.Settings.Default.UseEEG)
                        {
                           
                            eegTask = new Task("eegTask");
                            for (int i = 0; i < Properties.Settings.Default.EEGNumChannels; ++i)
                                eegTask.AIChannels.CreateVoltageChannel(Properties.Settings.Default.EEGDevice + "/ai" +
                                    (i).ToString(), "", AITerminalConfiguration.Nrse, -10.0, 10.0, AIVoltageUnits.Volts);
                            setGain(eegTask, (double)Properties.Settings.Default.EEGGain);
                            eegTask.Control(TaskAction.Verify);
                            eegSamplingRate = Properties.Settings.Default.EEGSamplingRate;
                        }

                        //Add channel to control Cineplex, if configured
                        if (checkBox_video.Checked)
                            triggerTask.DOChannels.CreateChannel(Properties.Settings.Default.CineplexDevice + "/Port0/line0:7", "",
                                ChannelLineGrouping.OneChannelForAllLines);

                        //Change gain based on comboBox values (1-100)
                        for (int i = 0; i < spikeTask.Count; ++i)
                            setGain(spikeTask[i], Properties.Settings.Default.A2Dgain);

                        //Verify the Tasks
                        for (int i = 0; i < spikeTask.Count; ++i)
                            spikeTask[i].Control(TaskAction.Verify);
                        //if (Properties.Settings.Default.UseSingleChannelPlayback)
                        //    spikeOutTask.Control(TaskAction.Verify);

                        //Get sampling rates, set to private variables
                        spikeSamplingRate = Properties.Settings.Default.RawSampleFrequency; 
                        lfpSamplingRate = Properties.Settings.Default.LFPSampleFrequency; 

                        //Version with videoTask as master clock
                        if (Properties.Settings.Default.UseCineplex)
                        {
                            for (int i = 0; i < spikeTask.Count; ++i)
                            {
                                spikeTask[i].Timing.ReferenceClockSource = videoTask.Timing.ReferenceClockSource;
                                spikeTask[i].Timing.ReferenceClockRate = videoTask.Timing.ReferenceClockRate;
                            }
                        }
                        else
                        {
                            string masterclock = "/" + Properties.Settings.Default.AnalogInDevice[0].ToString() + "/10MhzRefClock";//"OnboardClock";//
                            if (!Properties.Settings.Default.UseStimulator)
                            {
                                //Deal with non M-series devices (these can't use "ReferenceClockSource"
                                Device analogInDevice = DaqSystem.Local.LoadDevice(Properties.Settings.Default.AnalogInDevice[0]);

                                if (analogInDevice.ProductCategory == ProductCategory.MSeriesDaq || analogInDevice.ProductCategory == ProductCategory.XSeriesDaq)
                                    spikeTask[0].Timing.ReferenceClockSource = masterclock; //This will be the master clock
                            }
                            else
                            {

                                spikeTask[0].Timing.ReferenceClockSource = masterclock;//stimPulseTask.Timing.ReferenceClockSource;
                                spikeTask[0].Timing.ReferenceClockRate = 10000000.0; //stimPulseTask.Timing.ReferenceClockRate;
                            }
                            for (int i = 1; i < spikeTask.Count; ++i) //Set other analog in tasks to master clock
                            {
                                spikeTask[i].Timing.ReferenceClockSource = spikeTask[0].Timing.ReferenceClockSource;
                                spikeTask[i].Timing.ReferenceClockRate = spikeTask[0].Timing.ReferenceClockRate;
                            }
                        }
                        spikeTask[0].Timing.ConfigureSampleClock("", spikeSamplingRate,
                                SampleClockActiveEdge.Rising, SampleQuantityMode.ContinuousSamples, Convert.ToInt32(Properties.Settings.Default.RawSampleFrequency / 2));
                        for (int i = 1; i < spikeTask.Count; ++i)
                        {
                            //Pipe ai dev0's sample clock to slave devices
                            spikeTask[i].Timing.ConfigureSampleClock("/" + Properties.Settings.Default.AnalogInDevice[0] + "/ai/SampleClock", spikeSamplingRate,
                                SampleClockActiveEdge.Rising, SampleQuantityMode.ContinuousSamples, Convert.ToInt32(Properties.Settings.Default.RawSampleFrequency / 2));

                            //Trigger off of ai dev0's trigger
                            spikeTask[i].Triggers.StartTrigger.ConfigureDigitalEdgeTrigger("/" + Properties.Settings.Default.AnalogInDevice[0] +
                                "/ai/StartTrigger", DigitalEdgeStartTriggerEdge.Rising);

                            // Manually allocate buffer memory
                            //spikeTask[i].Stream.Buffer.InputBufferSize = DAQ_BUFFER_SIZE_SAMPLES;
                        }


                        if (Properties.Settings.Default.SeparateLFPBoard && Properties.Settings.Default.UseLFPs)
                        {
                            lfpTask.Timing.ReferenceClockSource = spikeTask[0].Timing.ReferenceClockSource;
                            lfpTask.Timing.ReferenceClockRate = spikeTask[0].Timing.ReferenceClockRate;
                            lfpTask.Timing.ConfigureSampleClock("", lfpSamplingRate,
                                SampleClockActiveEdge.Rising, SampleQuantityMode.ContinuousSamples, Convert.ToInt32(Properties.Settings.Default.LFPSampleFrequency / 2));

                            // Manually allocate buffer memory
                            //lfpTask.Stream.Buffer.InputBufferSize = DAQ_BUFFER_SIZE_SAMPLES;
                        }
                        else
                        {
                            Properties.Settings.Default.numLFPTasks = Properties.Settings.Default.numSpikeTasks;
                        }

                        if (Properties.Settings.Default.UseEEG)
                        {
                            eegTask.Timing.ReferenceClockSource = spikeTask[0].Timing.ReferenceClockSource;
                            eegTask.Timing.ReferenceClockRate = spikeTask[0].Timing.ReferenceClockRate;
                            eegTask.Timing.ConfigureSampleClock("", eegSamplingRate,
                                SampleClockActiveEdge.Rising, SampleQuantityMode.ContinuousSamples, Convert.ToInt32(Convert.ToDouble(Properties.Settings.Default.EEGSamplingRate) / 2));
                            
                            // Manually allocate buffer memory
                            //eegTask.Stream.Buffer.InputBufferSize = DAQ_BUFFER_SIZE_SAMPLES;
                        }

                        if (Properties.Settings.Default.UseCineplex)
                        {
                            if (checkBox_video.Checked)
                            {
                                triggerTask.Timing.ConfigureSampleClock("/" + Properties.Settings.Default.AnalogInDevice[0] + "/ai/SampleClock",
                                    spikeSamplingRate, SampleClockActiveEdge.Rising, SampleQuantityMode.FiniteSamples,
                                    3);
                            }
                            if (Properties.Settings.Default.SeparateLFPBoard && Properties.Settings.Default.UseLFPs)
                            {
                                lfpTask.Triggers.StartTrigger.ConfigureDigitalEdgeTrigger("/" +
                                    Properties.Settings.Default.AnalogInDevice[0] + "/ai/StartTrigger",
                                    DigitalEdgeStartTriggerEdge.Rising);
                            }
                            if (Properties.Settings.Default.UseEEG)
                            {
                                eegTask.Triggers.StartTrigger.ConfigureDigitalEdgeTrigger("/" +
                                    Properties.Settings.Default.AnalogInDevice[0] + "/ai/StartTrigger",
                                    DigitalEdgeStartTriggerEdge.Rising);
                            }
                        }

                        if (Properties.Settings.Default.UseStimulator && Properties.Settings.Default.RecordStimTimes)
                        {
                            try
                            {
                                
                                numStimReads = new List<int>(numDevices);
                                for (int i = 0; i < spikeTask.Count; ++i)
                                    numStimReads.Add(0);
                                stimTimeTask = new Task("stimTimeTask");
                                stimTimeTask.AIChannels.CreateVoltageChannel(Properties.Settings.Default.StimInfoDevice + "/ai16", "",
                                    AITerminalConfiguration.Nrse, -10.0, 10.0, AIVoltageUnits.Volts);
                                stimTimeTask.AIChannels.CreateVoltageChannel(Properties.Settings.Default.StimInfoDevice + "/ai0", "", AITerminalConfiguration.Nrse,
                                    -10.0, 10.0, AIVoltageUnits.Volts); //For triggers
                                
                                
                                // Pipe the spikeTasks sample clock to PFI14 on the stim board
                                DaqSystem.Local.ConnectTerminals(spikeTask[0].Timing.ReferenceClockSource,
                                    "/" + Properties.Settings.Default.StimulatorDevice.ToString() + "/PFI0");
                                
                                if (isNormalRecording)
                                    stimTimeTask.Timing.ReferenceClockSource = "/" + Properties.Settings.Default.StimulatorDevice.ToString() + "/PFI0";
                                else
                                    stimTimeTask.Timing.ReferenceClockSource = spikeTask[0].Timing.ReferenceClockSource;
                                stimTimeTask.Timing.ReferenceClockRate = spikeTask[0].Timing.ReferenceClockRate;
                                stimTimeTask.Timing.ConfigureSampleClock("", spikeSamplingRate,
                                    SampleClockActiveEdge.Rising, SampleQuantityMode.ContinuousSamples, Convert.ToInt32(Properties.Settings.Default.RawSampleFrequency / 2));
                                stimTimeTask.Triggers.StartTrigger.ConfigureDigitalEdgeTrigger(
                                    "/" + Properties.Settings.Default.AnalogInDevice[0] + "/ai/StartTrigger", DigitalEdgeStartTriggerEdge.Rising);
                                stimTimeTask.Control(TaskAction.Verify);

                                // stim Timing Channel settings object
                                StringCollection stimTimePhysChan = new StringCollection();
                                for (int i = 0; i < stimTimeTask.AIChannels.Count; ++i)
                                {
                                    stimTimePhysChan.Add(stimTimeTask.AIChannels[i].PhysicalName);
                                }
                                
                                // Write down the indicies corresponding to the portion of this task that will
                                // actually record stimulus infromation instead of aux analog input
                                stimTimeChanSet = new NRAIChannelCollection(stimTimePhysChan);
                                int[] stimTimeChannels = new int[] { 0, 1 };
                                stimTimeChanSet.SetupNumericalChannelOnly(stimTimeChannels);

                                // Manually allocate buffer memory
                                //stimTimeTask.Stream.Buffer.InputBufferSize = DAQ_BUFFER_SIZE_SAMPLES;

                               
                                Console.WriteLine("NRAcquisitionSetup complete");
                            }
                            catch (Exception e)
                            {
                                MessageBox.Show(e.Message);
                            }
                        }

                        //Setup scaling coefficients (to convert digital values to voltages)
                        scalingCoeffsSpikes = new List<double[]>(spikeTask.Count);
                        for (int i = 0; i < spikeTask.Count; ++i)
                            scalingCoeffsSpikes.Add(spikeTask[0].AIChannels[0].DeviceScalingCoefficients);
                        if (Properties.Settings.Default.SeparateLFPBoard)
                            scalingCoeffsLFPs = lfpTask.AIChannels[0].DeviceScalingCoefficients;
                        if (Properties.Settings.Default.UseEEG)
                            scalingCoeffsEEG = eegTask.AIChannels[0].DeviceScalingCoefficients;

                        // Setup auxiliary recording tasks
                        if (Properties.Settings.Default.useAuxAnalogInput)
                        {
                            // Set up the aux channel set
                            auxChanSet = new NRAIChannelCollection(Properties.Settings.Default.auxAnalogInChan);

                            if (Properties.Settings.Default.auxAnalogInDev == Properties.Settings.Default.StimInfoDevice
                                && Properties.Settings.Default.RecordStimTimes)
                            {
                                // In this case we are recording both stimulus times and aux analog input times on the same
                                // DAQ, so we need to just make the auxAnInTask reference the stimulus timing task
                                twoAITasksOnSingleBoard = true;
                                auxAnInTask = stimTimeTask;
                                auxChanSet.SetupAuxCollection(ref auxAnInTask);
                            }
                            else
                            {
                                // In this case there is no conflict for AI, so we can create a dedicated task for aux analog input
                                twoAITasksOnSingleBoard = false;
                                auxAnInTask = new Task("AuxiliaryAnalogInput");
                                auxChanSet.SetupAuxCollection(ref auxAnInTask);

                                auxAnInTask.Timing.ReferenceClockSource = spikeTask[0].Timing.ReferenceClockSource;
                                auxAnInTask.Timing.ReferenceClockRate = spikeTask[0].Timing.ReferenceClockRate;

                                //Pipe ai dev0's sample clock to slave devices
                                auxAnInTask.Timing.ConfigureSampleClock("", spikeSamplingRate,
                                    SampleClockActiveEdge.Rising, SampleQuantityMode.ContinuousSamples, Convert.ToInt32(Properties.Settings.Default.RawSampleFrequency / 2));
                                auxAnInTask.Triggers.StartTrigger.ConfigureDigitalEdgeTrigger("/Dev1/ai/StartTrigger", DigitalEdgeStartTriggerEdge.Rising);

                                // Manually allocate buffer memory
                                // auxAnInTask.Stream.Buffer.InputBufferSize = DAQ_BUFFER_SIZE_SAMPLES;

                                // Create space for the buffer
                                auxAnData = new double[auxChanSet.numericalChannels.Length, spikeBufferLength];

                            }

                        }

                        if (Properties.Settings.Default.useAuxDigitalInput)
                        {
                            auxDigInTask = new Task("AuxiliaryDigitalInput");
                            auxDigInTask.DIChannels.CreateChannel(Properties.Settings.Default.auxDigitalInPort,
                                "Auxiliary Digitial In", ChannelLineGrouping.OneChannelForAllLines);

                            auxDigInTask.Timing.ConfigureSampleClock("", spikeSamplingRate,
                                SampleClockActiveEdge.Rising, SampleQuantityMode.ContinuousSamples, Convert.ToInt32(Properties.Settings.Default.RawSampleFrequency / 2));
                            auxDigInTask.Timing.SampleClockSource = spikeTask[0].Timing.SampleClockTerminal;

                            // Manually allocate buffer memory
                            // auxDigInTask.Stream.Buffer.InputBufferSize = DAQ_BUFFER_SIZE_SAMPLES;
                        }

                        #region Setup_Plotting

                        numSnipsDisplayed = (int)numericUpDown_NumSnipsDisplayed.Value;

                       

                        #region PlotData_Buffers
                        //***********************
                        //Make PlotData buffers
                        //***********************
                        int downsample, numRows, numCols;
                        const double spikeplotlength = 0.25; //in seconds
                        switch (Properties.Settings.Default.NumChannels)
                        {
                            case 16:
                                numRows = numCols = 4;
                                downsample = 10;
                                break;
                            case 32:
                                numRows = numCols = 6;
                                downsample = 15;
                                break;
                            case 64:
                                numRows = numCols = 8;
                                downsample = 20; //if this gets really small, LFP data won't plot
                                break;
                            default:
                                numRows = numCols = 4;
                                downsample = 5;
                                break;
                        }

                        //Create plot colormap
                        NRBrainbow = (64).GenerateBrainbow();
                        NRSnipBrainbow = (64).GenerateSnipBrainbow();
                        NRUnitBrainbow = (64).GenerateUnitBrainbow();

                        //Initialize graphs
                        if (spikeGraph != null) { spikeGraph.Dispose(); spikeGraph = null; }
                        spikeGraph = new GridGraph();
                        int samplesPerPlot = (int)(Math.Ceiling(Properties.Settings.Default.ADCPollingPeriodSec * spikeSamplingRate / downsample) * (spikeplotlength / Properties.Settings.Default.ADCPollingPeriodSec));
                        spikeGraph.setup(numRows, numCols, samplesPerPlot, false, 1 / 4.0, spikeTask[0].AIChannels.All.RangeHigh * 2.0);
                        spikeGraph.setMinMax(0, (float)(samplesPerPlot * numCols) - 1,
                            (float)(spikeTask[0].AIChannels.All.RangeLow * (numRows * 2 - 1)), (float)(spikeTask[0].AIChannels.All.RangeHigh));
                        spikeGraph.Dock = DockStyle.Fill;
                        spikeGraph.Parent = tabPage_spikes;

                        if (Properties.Settings.Default.UseLFPs)
                        {
                            if (lfpGraph != null) { lfpGraph.Dispose(); lfpGraph = null; }
                            lfpGraph = new RowGraph();
                            lfpGraph.setup(numChannels, (int)((Math.Ceiling(Properties.Settings.Default.ADCPollingPeriodSec * lfpSamplingRate / downsample) * (5 / Properties.Settings.Default.ADCPollingPeriodSec))),
                                5.0, spikeTask[0].AIChannels.All.RangeHigh * 2.0);
                            if (Properties.Settings.Default.SeparateLFPBoard)
                                lfpGraph.setMinMax(0, 5 * (int)(Math.Ceiling(Properties.Settings.Default.ADCPollingPeriodSec * lfpSamplingRate / downsample) / Properties.Settings.Default.ADCPollingPeriodSec) - 1,
                                    (float)(lfpTask.AIChannels.All.RangeLow * (numChannels * 2 - 1)), (float)(lfpTask.AIChannels.All.RangeHigh));
                            else
                                lfpGraph.setMinMax(0, 5 * (int)(Math.Ceiling(Properties.Settings.Default.ADCPollingPeriodSec * lfpSamplingRate / downsample) / Properties.Settings.Default.ADCPollingPeriodSec) - 1,
                                    (float)(spikeTask[0].AIChannels.All.RangeLow * (numChannels * 2 - 1)), (float)(spikeTask[0].AIChannels.All.RangeHigh));
                            lfpGraph.Dock = DockStyle.Fill;
                            lfpGraph.Parent = tabPage_LFPs;
                        }

                        if (Properties.Settings.Default.ProcessMUA)
                        {
                            if (muaGraph != null) { muaGraph.Dispose(); muaGraph = null; }
                            muaGraph = new RowGraph();
                            muaGraph.setup(numChannels, (int)((Math.Ceiling(Properties.Settings.Default.ADCPollingPeriodSec * muaSamplingRate / downsample) * (5 / Properties.Settings.Default.ADCPollingPeriodSec))),
                                5.0, spikeTask[0].AIChannels.All.RangeHigh * 2.0);
                            muaGraph.setMinMax(0, 5 * (int)(Math.Ceiling(Properties.Settings.Default.ADCPollingPeriodSec * muaSamplingRate / downsample) / Properties.Settings.Default.ADCPollingPeriodSec) - 1,
                                    (float)(spikeTask[0].AIChannels.All.RangeLow * (numChannels * 2 - 1)), (float)(spikeTask[0].AIChannels.All.RangeHigh));
                            muaGraph.Dock = DockStyle.Fill;
                            muaGraph.Parent = tabPage_MUA;

                            muaPlotData = new PlotDataRows(numChannels, downsample, (int)(muaSamplingRate * 5), muaSamplingRate,
                                    (float)spikeTask[0].AIChannels.All.RangeHigh * 2F, 0.5, 5, Properties.Settings.Default.ADCPollingPeriodSec);
                            //muaPlotData.setGain(Properties.Settings.Default.LFPDisplayGain);

                            //muaGraph.setDisplayGain(Properties.Settings.Default.LFPDisplayGain);
                            muaPlotData.dataAcquired += new PlotData.dataAcquiredHandler(muaPlotData_dataAcquired);
                        }

                        if (Properties.Settings.Default.UseEEG)
                        {
                            if (eegGraph != null) { eegGraph.Dispose(); eegGraph = null; }
                            eegGraph = new RowGraph();
                            eegGraph.setup(Properties.Settings.Default.EEGNumChannels, (int)((Math.Ceiling(Properties.Settings.Default.ADCPollingPeriodSec * eegSamplingRate / downsample) * (5 / Properties.Settings.Default.ADCPollingPeriodSec))),
                                5.0, eegTask.AIChannels.All.RangeHigh * 2.0);
                            eegGraph.setMinMax(0, 5 * (int)(Math.Ceiling(Properties.Settings.Default.ADCPollingPeriodSec * eegSamplingRate / downsample) / Properties.Settings.Default.ADCPollingPeriodSec) - 1,
                                    (float)(eegTask.AIChannels.All.RangeLow * (Properties.Settings.Default.EEGNumChannels * 2 - 1)), (float)(eegTask.AIChannels.All.RangeHigh));
                            eegGraph.Dock = DockStyle.Fill;
                            eegGraph.Parent = tabPage_EEG;
                        }

                        resetSpkWfm(); //Take care of spike waveform graph

                        double ampdec = (1 / Properties.Settings.Default.PreAmpGain);

                        spikePlotData = new PlotDataGrid(numChannels, downsample, (int)(spikeSamplingRate), spikeSamplingRate,
                            (float)(spikeTask[0].AIChannels.All.RangeHigh * 2.0), numRows, numCols, spikeplotlength,
                            Properties.Settings.Default.ChannelMapping, Properties.Settings.Default.ADCPollingPeriodSec);
                        spikePlotData.dataAcquired += new PlotData.dataAcquiredHandler(spikePlotData_dataAcquired);
                        spikePlotData.setGain(Properties.Settings.Default.SpikeDisplayGain);
                        spikeGraph.setDisplayGain(Properties.Settings.Default.SpikeDisplayGain);

                        if (Properties.Settings.Default.UseLFPs)
                        {
                            if (Properties.Settings.Default.SeparateLFPBoard)
                                lfpPlotData = new PlotDataRows(numChannels, downsample, (int)(lfpSamplingRate * 5), lfpSamplingRate,
                                    (float)lfpTask.AIChannels.All.RangeHigh * 2F, 0.5, 5, Properties.Settings.Default.ADCPollingPeriodSec);
                            else lfpPlotData = new PlotDataRows(numChannels, downsample, (int)(lfpSamplingRate * 5), lfpSamplingRate,
                                    (float)spikeTask[0].AIChannels.All.RangeHigh * 2F, 0.5, 5, Properties.Settings.Default.ADCPollingPeriodSec);
                            lfpPlotData.setGain(Properties.Settings.Default.LFPDisplayGain);

                            lfpGraph.setDisplayGain(Properties.Settings.Default.LFPDisplayGain);
                            lfpPlotData.dataAcquired += new PlotData.dataAcquiredHandler(lfpPlotData_dataAcquired);
                        }

                        waveformPlotData = new EventPlotData(numChannels, spikeDet.NumPre + spikeDet.NumPost + 1, (float)(spikeTask[0].AIChannels.All.RangeHigh * 2F),
                            numRows, numCols, numSnipsDisplayed, Properties.Settings.Default.ChannelMapping);
                        waveformPlotData.setGain(Properties.Settings.Default.SpkWfmDisplayGain);
                        spkWfmGraph.setDisplayGain(Properties.Settings.Default.SpkWfmDisplayGain);
                        waveformPlotData.dataAcquired += new EventPlotData.dataAcquiredHandler(waveformPlotData_dataAcquired);
                        waveformPlotData.start();
                        #endregion



                        if (Properties.Settings.Default.UseEEG)
                        {
                            eegPlotData = new PlotDataRows(Properties.Settings.Default.EEGNumChannels, downsample, (int)(eegSamplingRate * 5), eegSamplingRate,
                                    (float)eegTask.AIChannels.All.RangeHigh * 2F, 0.5, 5, Properties.Settings.Default.ADCPollingPeriodSec);
                            eegPlotData.setGain(Properties.Settings.Default.EEGDisplayGain);

                            eegGraph.setDisplayGain(Properties.Settings.Default.EEGDisplayGain);
                            eegPlotData.dataAcquired += new PlotData.dataAcquiredHandler(eegPlotData_dataAcquired);
                        }

                        if (Properties.Settings.Default.useAuxAnalogInput)
                        {
                            // Remove existing plots
                            for (int i = scatterGraph_AuxAnalogData.Plots.Count-1; i > 0; --i)
                            {
                                scatterGraph_AuxAnalogData.Plots.RemoveAt(i);
                            }
                            // Initialize the aux data scatter graph with a plot for each aux Analog channel
                            for (int i = 0; i < Properties.Settings.Default.auxAnalogInChan.Count-1; ++i)
                            {
                                ScatterPlot p = new ScatterPlot();
                                scatterGraph_AuxAnalogData.Plots.Add(p);
                            }

                            // Initialize the controller
                            auxInputGraphController = new ScatterGraphController(ref scatterGraph_AuxAnalogData);

                            // Make history selector reflect current limits on input
                            //slide_AnalogDispMaxVoltage.Range = new Range(0.05, 10);
                            //slide_AnalogDispWidth.Range = new Range(2*Properties.Settings.Default.ADCPollingPeriodSec, Properties.Settings.Default.datSrvBufferSizeSec);

                        }
                        #endregion

                        #region Setup_Filters
                        //Setup filters, based on user's input
                        resetSpikeFilter();
                        if (Properties.Settings.Default.UseLFPs) resetLFPFilter();
                        resetEEGFilter();

                        muaFilter = new Filters.MUAFilter(
                            numChannels, spikeSamplingRate, spikeBufferLength, 
                            Properties.Settings.Default.MUAHighCutHz, 
                            Properties.Settings.Default.MUAFilterOrder, 
                            MUA_DOWNSAMPLE_FACTOR, 
                            Properties.Settings.Default.ADCPollingPeriodSec);

                        #endregion

                        #region Setup_DataStorage
                        //Initialize data storing matrices
                      //  numChannels = Properties.Settings.Default.NumChannels; 

                        numSpikeReads = new int[spikeTask.Count];

                        filtSpikeData = new rawType[numChannels][];

                        if (Properties.Settings.Default.UseLFPs)
                        {
                            filtLFPData = new rawType[numChannels][];
                            finalLFPData = new rawType[numChannels][];
                            for (int i = 0; i < filtSpikeData.GetLength(0); ++i)
                            {
                                if (Properties.Settings.Default.SeparateLFPBoard)
                                    filtLFPData[i] = new rawType[lfpBufferLength];
                                else
                                    filtLFPData[i] = new rawType[spikeBufferLength];
                            }
                        }

                        if (Properties.Settings.Default.ProcessMUA)
                        {
                            muaData = new double[numChannels][];
                            for (int c = 0; c < numChannels; ++c)
                                muaData[c] = new double[spikeBufferLength / MUA_DOWNSAMPLE_FACTOR];
                        }

                        if (Properties.Settings.Default.UseEEG)
                        {
                            
                            filtEEGData = new double[Properties.Settings.Default.EEGNumChannels][];
                            for (int i = 0; i < filtEEGData.GetLength(0); ++i)
                            {
                                filtEEGData[i] = new double[eegBufferLength];
                            }
                        }

                        for (int i = 0; i < filtSpikeData.GetLength(0); ++i)
                        {
                            filtSpikeData[i] = new rawType[spikeBufferLength];
                            if (Properties.Settings.Default.UseLFPs)
                                finalLFPData[i] = new rawType[lfpBufferLength];
                        }

                        if (Properties.Settings.Default.UseStimulator)
                        {
                            stimDataBuffer = new double[STIM_BUFFER_LENGTH];
                            stimJump = (double)spikeSamplingRate * 0.0001; //num. indices in 100 us of data
                        }

                        stimIndices = new List<StimTick>(5);
                        //if devices refresh rate is reset, need to reset SALPA
                        if (checkBox_SALPA.Checked)
                            resetSALPA();
                        if (spikeDet != null && isNormalRecording)
                            setSpikeDetector();
                        if (spikeDet.spikeDetector == null)
                            setSpikeDetector();

                        #endregion

                        #region Verify Tasks
                        if (Properties.Settings.Default.UseStimulator && Properties.Settings.Default.RecordStimTimes)
                            stimTimeTask.Control(TaskAction.Verify);
                        if (Properties.Settings.Default.UseEEG)
                            eegTask.Control(TaskAction.Verify);
                        if (Properties.Settings.Default.SeparateLFPBoard && Properties.Settings.Default.UseLFPs)
                            lfpTask.Control(TaskAction.Verify);
                        if (checkBox_video.Checked)
                            triggerTask.Control(TaskAction.Verify);
                        for (int i = 0; i < spikeTask.Count; ++i)
                            spikeTask[i].Control(TaskAction.Verify);
                        if (Properties.Settings.Default.useAuxAnalogInput)
                            auxAnInTask.Control(TaskAction.Verify);
                        if (Properties.Settings.Default.useAuxDigitalInput)
                            auxDigInTask.Control(TaskAction.Verify);
                        #endregion

                        SetupFileWriting();

                        //Set callbacks for data acq.
                        spikeReader = new List<AnalogMultiChannelReader>(spikeTask.Count);
                        for (int i = 0; i < spikeTask.Count; ++i)
                        {
                            spikeReader.Add(new AnalogMultiChannelReader(spikeTask[i].Stream));
                            spikeReader[i].SynchronizeCallbacks = true;
                        }
                        //if (Properties.Settings.Default.UseSingleChannelPlayback)
                        //    spikeOutWriter = new AnalogSingleChannelWriter(spikeOutTask.Stream);
                        if (checkBox_video.Checked)
                            triggerWriter = new DigitalSingleChannelWriter(triggerTask.Stream);

                        //if (Properties.Settings.Default.UseSingleChannelPlayback)
                        //    spikeOutWriter.SynchronizeCallbacks = false; //These don't use UI, so they don't need to be synched

                        spikeCallback = new AsyncCallback(AnalogInCallback_spikes);

                        if (Properties.Settings.Default.UseStimulator && Properties.Settings.Default.RecordStimTimes)
                        {
                            stimTimeReader = new AnalogMultiChannelReader(stimTimeTask.Stream);
                        }

                        if (Properties.Settings.Default.SeparateLFPBoard && Properties.Settings.Default.UseLFPs)
                        {
                            lfpReader = new AnalogUnscaledReader(lfpTask.Stream);
                            lfpReader.SynchronizeCallbacks = true;
                            lfpCallback = new AsyncCallback(AnalogInCallback_LFPs);
                        }
                        if (Properties.Settings.Default.UseEEG)
                        {
                            eegReader = new AnalogUnscaledReader(eegTask.Stream);
                            eegReader.SynchronizeCallbacks = true;
                            eegCallback = new AsyncCallback(AnalogInCallback_EEG);
                        }

                        if (Properties.Settings.Default.useAuxAnalogInput)
                        {
                            auxAnReader = new AnalogMultiChannelReader(auxAnInTask.Stream);
                            auxAnReader.SynchronizeCallbacks = true;
                            auxAnCallback = new AsyncCallback(AnalogInCallback_AuxAn);
                        }

                        if (Properties.Settings.Default.useAuxDigitalInput)
                        {
                            auxDigReader = new DigitalSingleChannelReader(auxDigInTask.Stream);
                            auxDigReader.SynchronizeCallbacks = true;
                            auxDigCallback = new AsyncCallback(AnalogInCallback_AuxDig);
                        }

                        //Setup background workers for data processing
                        bwSpikes = new List<BackgroundWorker>(spikeTask.Count);
                        bwIsRunning = new bool[spikeTask.Count];
                        for (int i = 0; i < spikeTask.Count; ++i)
                        {
                            bwSpikes.Add(new BackgroundWorker());
                            bwSpikes[i].DoWork += new DoWorkEventHandler(bwSpikes_DoWork);
                            bwSpikes[i].RunWorkerCompleted += new RunWorkerCompletedEventHandler(bwSpikes_RunWorkerCompleted);
                            bwSpikes[i].WorkerSupportsCancellation = true;
                        }


                        //Make persistent buffers for spikeData
                        spikeData = new List<AnalogWaveform<double>[]>(spikeReader.Count);
                        for (int i = 0; i < spikeReader.Count; ++i)
                        {
                            spikeData.Add(new AnalogWaveform<double>[numChannelsPerDev]);
                            for (int j = 0; j < numChannelsPerDev; ++j)
                                spikeData[i][j] = new AnalogWaveform<double>(spikeBufferLength);
                        }

                        //Make channel playback task
                        if (Properties.Settings.Default.UseSingleChannelPlayback)
                            BNCOutput = new ChannelOutput(spikeSamplingRate, 0.1, Properties.Settings.Default.ADCPollingPeriodSec, spikeTask[0],
                                Properties.Settings.Default.SingleChannelPlaybackDevice, 0);


                    }
                    catch (Exception exception)
                    {
                        //Display Errors
                        this.Cursor = Cursors.Default;
                        MessageBox.Show(exception.Message);
                        reset();
                    }


                    // Set up the DataSrv object. This is an object that publishes a nice large data history
                    // for use in closed loop control and other things
                    if (datSrv != null)
                        datSrv = null;

                    datSrv = new DataSrv(
                        Properties.Settings.Default.datSrvBufferSizeSec,
                        checkBox_SALPA.Checked,
                        SALPA_WIDTH,
                        checkBox_spikesFilter.Checked,
                        spikeDet.spikeDetectionLag
                        );

                    // Set the number of units if appropriate
                    if (spikeDet.spikeSorter != null)
                        datSrv.SetNumberOfUnits(spikeDet.spikeSorter.totalNumberOfUnits, spikeDet.spikeSorter.unit2Channel);

                    Debugger = new Logger();
                    Debugger.GrabTimer(spikeTask[0]);

                    //Send debug output to the user's application data folder 
                    Debugger.SetPath(Path.Combine(Properties.Settings.Default.neurorighterAppDataPath, "neurorighter-log.txt"));


                    //Tell neuroRighter that the tasks now exist
                    taskRunning = true;
                }
                else
                {
                    Console.WriteLine("NRAcquisitionSetup was called while a task was running, and therefore setup did not execute.  Perhaps this should have thrown an error");
                }
            }

            //update gui at the end
            // Modify the UI, so user doesn't try running multiple instances of tasks
            
           
            spikeDet.numPreSamples.Enabled = false;
            spikeDet.numPostSamples.Enabled = false;
            settingsToolStripMenuItem.Enabled = false;
            
            button_Train.Enabled = false;
            button_SetRecordingStreams.Enabled = false;
            switch_record.Enabled = false;
            //processingSettingsToolStripMenuItem.Enabled = false;
           
            button_startStimFromFile.Enabled = false;
            button_startClosedLoopStim.Enabled = false;
            checkBox_SALPA.Enabled = false;
            
            //numericUpDown_NumSnipsDisplayed.Enabled = false;
            button_startClosedLoopStim.Enabled = false;
            button_scaleUp.Enabled = true;
            button_scaleDown.Enabled = true;
            button_scaleReset.Enabled = true;

            // Disable spike detector saving while running
            spikeDet.DisableFileMenu();
            Console.WriteLine("NRAcquisitionSetup successfully executed");
            this.Cursor = Cursors.Default;
            return false;
        }

        // Start all the tasks having to do with recording
        private void NRStartRecording()
        {
            lock (this)
            {
                try
                {
                    // Take care of buttons
                    buttonStop.Enabled = true;
                    buttonStart.Enabled = false;

                    // integers tracking the number of reads performed
                    trackingReads = new int[2];
                    trackingProc = new int[2];
                    trackingDigReads = new int();

                    //Start tasks (start LFP first, since it's triggered off spikeTask) and timer (for file writing)
                    if (checkBox_video.Checked)
                    {
                        byte[] b_array = new byte[3] { 255, 255, 255 };
                        DigitalWaveform wfm = new DigitalWaveform(3, 8, DigitalState.ForceDown);
                        wfm = NationalInstruments.DigitalWaveform.FromPort(b_array);
                        triggerWriter.BeginWriteWaveform(true, wfm, null, null);
                    }


                    if (Properties.Settings.Default.useAuxDigitalInput)
                        auxDigInTask.Start();
                    if (Properties.Settings.Default.UseStimulator && Properties.Settings.Default.RecordStimTimes)
                        stimTimeTask.Start();
                    if (Properties.Settings.Default.useAuxAnalogInput && !twoAITasksOnSingleBoard)
                        auxAnInTask.Start();
                    if (Properties.Settings.Default.SeparateLFPBoard && Properties.Settings.Default.UseLFPs)
                        lfpTask.Start();
                    if (Properties.Settings.Default.UseEEG)
                        eegTask.Start();
                    for (int i = spikeTask.Count - 1; i >= 0; --i)
                        spikeTask[i].Start(); //Start first task last, since it has master clock

                    // Start data collection
                    if (Properties.Settings.Default.useAuxAnalogInput && !twoAITasksOnSingleBoard)
                        auxAnReader.BeginMemoryOptimizedReadMultiSample(spikeBufferLength, auxAnCallback,null, auxAnData); 
                    if (Properties.Settings.Default.useAuxDigitalInput)
                        auxDigReader.BeginReadMultiSamplePortUInt32(spikeBufferLength, auxDigCallback, auxDigReader);
                    if (Properties.Settings.Default.SeparateLFPBoard && Properties.Settings.Default.UseLFPs)
                        lfpReader.BeginReadInt16(lfpBufferLength, lfpCallback, lfpReader);
                    if (Properties.Settings.Default.UseEEG)
                        eegReader.BeginReadInt16(eegBufferLength, eegCallback, eegReader);
                    for (int i = 0; i < spikeReader.Count; ++i)
                        spikeReader[i].BeginMemoryOptimizedReadWaveform(spikeBufferLength, spikeCallback, i,
                            spikeData[i]);

                    //Set start time
                    experimentStartTime = DateTime.Now;
                    double sec2add = 60 * Convert.ToDouble(numericUpDown_timedRecordingDuration.Value) + Convert.ToDouble(numericUpDown_timedRecordingDurationSeconds.Value);
                    timedRecordingStopTime = DateTime.Now.AddSeconds(sec2add);
                    timer_timeElapsed.Enabled = true;

                    if (checkBox_video.Checked)
                    {
                        triggerTask.WaitUntilDone();
                        triggerTask.Dispose();
                    }
                }
                catch (Exception e)
                {
                    MessageBox.Show(e.Message);
                }

            }
        }

        // Method to set up the output (dig, aux, stim) side of neurorighter
        // acquisition setup must have been called previously
        private Task NROutputSetup()
        {
            if (stimSrv != null)
                stimSrv = null;
            stimSrv = new StimSrv((int)(Properties.Settings.Default.DACPollingPeriodSec * STIM_SAMPLING_FREQ), STIM_SAMPLING_FREQ, spikeTask[0], Debugger,Properties.Settings.Default.stimRobust
                );
            stimSrv.Setup();
            stimSrv.StartAllTasks();
            return stimSrv.buffLoadTask;
        }

        private void NROutputShutdown()
        {
            if (stimSrv != null)
            {
                stimSrv.StopLoading();
                stimSrv.StopAllBuffers();
                
                //this is admittedly a bit of a hack, but it should always work: (need to wait atleast one more buffload so that daqs can clear)
                //System.Threading.Thread.Sleep((int)(Properties.Settings.Default.DACPollingPeriodSec * 2000));
                
                stimSrv = null;
                //Debugger.Close();
            }
            else
            {
                Console.WriteLine("NROutputShutdown called but stimSrv does not exist");
            }
            Console.WriteLine("NROutputShutdown complete");
        }

        // Instantiate the data stream writers within the recordingSettings object
        private void SetupFileWriting()
        {
            lock (this)
            {
                // Formate the raw data scalers, needed regardless of whether user is recording or not
                neuralDataScaler.Set16BitResolution(
                    spikeTask[0].AIChannels.All.RangeHigh / (Int16.MaxValue * Properties.Settings.Default.PreAmpGain));

                if (Properties.Settings.Default.useAuxAnalogInput)
                    auxDataScaler.Set16BitResolution(auxAnInTask.AIChannels.All.RangeHigh / Int16.MaxValue);

                if (switch_record.Value)
                {
                    DateTime dt = DateTime.Now; //Get current time (local to computer)
                    try
                    {
                        // Tell NR that that its about to encounter the first write
                        firstRawWrite = new bool[spikeTask.Count];
                        for (int i = 0; i < firstRawWrite.Length; ++i)
                        {
                            firstRawWrite[i] = true;
                        }

                        // 1. spk streams
                        recordingSettings.Setup("rawspk", spikeTask[0], spikeDet.NumPre, spikeDet.NumPost, false);
                        recordingSettings.Setup("salpaspk", spikeTask[0], spikeDet.NumPre, spikeDet.NumPost, false);
                        if (spikeDet.spikeSorter != null && spikeDet.IsEngaged)
                            recordingSettings.Setup("spk", spikeTask[0], spikeDet.NumPre, spikeDet.NumPost, true);
                        else
                            recordingSettings.Setup("spk", spikeTask[0], spikeDet.NumPre, spikeDet.NumPost, false);

                        // 2. raw streams
                        recordingSettings.Setup("raw", spikeTask[0]);
                        recordingSettings.Setup("salpa", spikeTask[0]);
                        recordingSettings.Setup("spkflt", spikeTask[0]);
                        if (Properties.Settings.Default.SeparateLFPBoard)
                            recordingSettings.Setup("lfp", lfpTask, lfpSamplingRate);
                        else
                            recordingSettings.Setup("lfp", spikeTask[0], lfpSamplingRate);
                        recordingSettings.Setup("eeg", eegTask, eegSamplingRate);

                        // 3. other
                        recordingSettings.Setup("stim", spikeTask[0]);
                        if (auxAnInTask != null)
                            recordingSettings.Setup("aux", auxAnInTask,auxChanSet.numericalChannels.Length);
                        recordingSettings.Setup("dig", spikeTask[0]);
                    }
                    catch (System.IO.IOException ex)
                    {
                        MessageBox.Show(ex.Message);
                        reset();
                    }
                }
            }
        }

        // Stop data aquisition and clean up
        private void buttonStop_Click(object sender, EventArgs e)
        {
            Thread.Sleep(200); // Let file writing etc. finish
            reset();
            UpdateRecordingSettings();
        }

        private void showConsoleToolStripMenuItem_Click(object sender, EventArgs e)
        {
            nrConsole.ShowConsole();
        }

        
    }
}