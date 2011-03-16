﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using NationalInstruments;
using NationalInstruments.DAQmx;

namespace NeuroRighter.FileWriting
{
    sealed internal partial class RecordingSetup : Form
    {

        // The base file name + parameters
        private string fid;
        private int numElectrodes;

        // These bits determine the streams to be recorded
        internal bool recordSpike;
        internal bool recordRaw;
        internal bool recordSALPA;
        internal bool recordSpikeFilt;
        internal bool recordLFP;
        internal bool recordEEG;
        internal bool recordMUA;
        internal bool recordStim;
        internal bool recordAuxDig;
        internal bool recordAuxAnalog;

        // The file writers
        internal SpikeFileOutput spkOut;
        internal FileOutput rawOut;
        internal FileOutput salpaOut;
        internal FileOutput spkFiltOut;
        internal FileOutput lfpOut;
        internal FileOutput eegOut;
        internal StimFileOutput stimOut;
        //internal FileOutput auxAnalogOut;
        //internal DigFileOutput auxDigitalOut;

        //

        // Delegates for informing mainform of settings change
        internal delegate void resetRecordingSettingsHandler(object sender, EventArgs e);
        internal event resetRecordingSettingsHandler SettingsHaveChanged;

        public RecordingSetup()
        {
            InitializeComponent();

            checkBox_RecordLFP.Enabled = Properties.Settings.Default.UseLFPs;
            checkBox_RecordEEG.Enabled = Properties.Settings.Default.UseEEG;
            checkBox_RecordStim.Enabled = Properties.Settings.Default.RecordStimTimes;

            checkBox_RecordMUA.Enabled = false; // TODO: CREATE SUPPORT FOR MUA
            checkBox_RecordAuxAnalog.Enabled = false; // TODO: CREATE SUPPORT FOR AUX INPUT
            checkBox_RecordAuxDig.Enabled = false; // TODO: CREATE SUPPORT FOR AUX INPUT

            // Set recording parameters
            ResetStreams2Record();
        }

        internal void Refresh()
        {
            checkBox_RecordLFP.Enabled = Properties.Settings.Default.UseLFPs;
            checkBox_RecordEEG.Enabled = Properties.Settings.Default.UseEEG;
            checkBox_RecordStim.Enabled = Properties.Settings.Default.RecordStimTimes;

            checkBox_RecordMUA.Enabled = false; // TODO: CREATE SUPPORT FOR MUA
            checkBox_RecordAuxAnalog.Enabled = false; // TODO: CREATE SUPPORT FOR AUX INPUT
            checkBox_RecordAuxDig.Enabled = false; // TODO: CREATE SUPPORT FOR AUX INPUT

            // Set recording parameters
            ResetStreams2Record();
        }

        internal void SetFID(string fid)
        {
            this.fid = fid;
        }

        internal void SetNumElectrodes(int numElectrodes)
        {
            this.numElectrodes = numElectrodes;
        }

        // For spike-type streams
        internal void Setup(string dataType, Task dataTask, int numPreSamp, int numPostSamp)
        {
            //Create the nessesary file writers
            switch(dataType)
            {
                case "spk":
                    // Check if we need to create this stream
                    if (recordSpike)
                    {
                        spkOut = new SpikeFileOutput(fid, numElectrodes,
                            (int)dataTask.Timing.SampleClockRate,
                            Convert.ToInt32(numPreSamp + numPostSamp) + 1,
                            dataTask, "." + dataType);
                    }
                    break;
                default:
                    Console.WriteLine("Unknown data type specified during RecordingSetup.Setup()");
                    break;

            }
        }
           
        // For down-sampled, raw-type streams
        internal void Setup(string dataType, Task dataTask, int samplingRate)
        {
            //Create the nessesary file writers
            switch(dataType)
            {
                case "lfp":
                    // Check if we need to create this stream
                    if (recordLFP)
                    {
                        if (Properties.Settings.Default.SeparateLFPBoard)
                            lfpOut = new FileOutput(fid, numElectrodes, samplingRate, 1, dataTask,
                                         "." + dataType, Properties.Settings.Default.PreAmpGain);
                        else
                        {
                            if (numElectrodes == 64 && Properties.Settings.Default.ChannelMapping == "invitro")
                                lfpOut = new FileOutputRemapped(fid, numElectrodes, samplingRate, 1, dataTask,
                                    "." + dataType, Properties.Settings.Default.PreAmpGain);
                            else
                                lfpOut = new FileOutput(fid, numElectrodes, samplingRate, 1, dataTask,
                                    "." + dataType, Properties.Settings.Default.PreAmpGain);
                        }
                    }
                    break;
                case "eeg":
                    // Check if we need to create this stream
                    if (recordEEG)
                    {
                        if (numElectrodes == 64 && Properties.Settings.Default.ChannelMapping == "invitro")
                            eegOut = new FileOutputRemapped(fid, numElectrodes, samplingRate, 1, dataTask,
                                "." + dataType, Properties.Settings.Default.PreAmpGain);
                        else
                            eegOut = new FileOutput(fid, numElectrodes, samplingRate, 1, dataTask,
                                    "." + dataType, Properties.Settings.Default.PreAmpGain);
                    }
                    break;
                default:
                    Console.WriteLine("Unknown data type specified during RecordingSetup.Setup()");
                    break;


            }
        }

        // For full sampled streams
        internal void Setup(string dataType, Task dataTask)
        {
            //Create the nessesary file writers
            switch (dataType)
            {
                case "raw":
                    // Check if we need to create this stream
                    if (recordRaw)
                    {
                        if (numElectrodes == 64 && Properties.Settings.Default.ChannelMapping == "invitro")
                            rawOut = new FileOutputRemapped(fid, numElectrodes,
                                (int)dataTask.Timing.SampleClockRate, 1, dataTask,
                                "." + dataType, Properties.Settings.Default.PreAmpGain);
                        else
                            rawOut = new FileOutput(fid, numElectrodes,
                                (int)dataTask.Timing.SampleClockRate, 1, dataTask,
                                "." + dataType, Properties.Settings.Default.PreAmpGain);
                    }
                    break;
                case "salpa":
                    // Check if we need to create this stream
                    if (recordSALPA)
                    {
                        if (numElectrodes == 64 && Properties.Settings.Default.ChannelMapping == "invitro")
                            salpaOut = new FileOutputRemapped(fid, numElectrodes,
                                (int)dataTask.Timing.SampleClockRate, 1, dataTask,
                                "." + dataType, Properties.Settings.Default.PreAmpGain);
                        else
                            salpaOut = new FileOutput(fid, numElectrodes,
                                (int)dataTask.Timing.SampleClockRate, 1, dataTask,
                                "." + dataType, Properties.Settings.Default.PreAmpGain);
                    }
                    break;
                case "spkflt":
                    // Check if we need to create this stream
                    if (recordSpikeFilt)
                    {
                        if (numElectrodes == 64 && Properties.Settings.Default.ChannelMapping == "invitro")
                            spkFiltOut = new FileOutputRemapped(fid, numElectrodes,
                                (int)dataTask.Timing.SampleClockRate, 1, dataTask,
                                "." + dataType, Properties.Settings.Default.PreAmpGain);
                        else
                            spkFiltOut = new FileOutput(fid, numElectrodes,
                                (int)dataTask.Timing.SampleClockRate,1, dataTask,
                                "." + dataType, Properties.Settings.Default.PreAmpGain);
                    }
                    break;
                case "stim":
                    // Check if we need to create this stream
                    if (recordStim)
                    {
                        stimOut = new StimFileOutput(fid,(int)dataTask.Timing.SampleClockRate,
                            "." + dataType);
                    }
                    break;
                default:
                    Console.WriteLine("Unknown data type specified during RecordingSetup.Setup()");
                    break;
            }
        } 

        // Cleanup
        internal void Flush()
        {
            if (spkOut != null) { spkOut.flush(); spkOut = null; }
            if (rawOut != null) { rawOut.flush(); rawOut = null; }
            if (salpaOut != null) { salpaOut.flush(); salpaOut = null; }
            if (spkFiltOut != null) { spkFiltOut.flush(); spkFiltOut = null;} 
            if (lfpOut != null) { lfpOut.flush(); lfpOut = null;};
            if (stimOut != null) {stimOut.flush(); stimOut = null;}

        }

        internal void SetSalpaAccess(bool recSalpaEnable)
        {
            if (!recSalpaEnable)
                checkBox_RecordSALPA.Checked = false;
            checkBox_RecordSALPA.Enabled = recSalpaEnable;
        }


        internal void SetSpikeFiltAccess(bool recSpikeEnable)
        {
            if (!recSpikeEnable)
                checkBox_RecordSpikeFilt.Checked = false;
            checkBox_RecordSpikeFilt.Enabled = recSpikeEnable;
        }
      
        private void checkBox_RecordRaw_CheckedChanged(object sender, EventArgs e)
        {
            // Set recording parameters
            ResetStreams2Record();
            SettingsHaveChanged(this, e);
        }

        private void checkBox_RecordSALPA_CheckedChanged(object sender, EventArgs e)
        {
            // Set recording parameters
            ResetStreams2Record();
            SettingsHaveChanged(this, e);
        }

        private void checkBox_RecordSpikeFilt_CheckedChanged(object sender, EventArgs e)
        {
            // Set recording parameters
            ResetStreams2Record();
            SettingsHaveChanged(this, e);
        }

        private void checkBox_RecordLFP_CheckedChanged(object sender, EventArgs e)
        {
            // Set recording parameters
            ResetStreams2Record();
            SettingsHaveChanged(this, e);
        }

        private void checkBox_RecordEEG_CheckedChanged(object sender, EventArgs e)
        {
            // Set recording parameters
            ResetStreams2Record();
            SettingsHaveChanged(this, e);
        }

        private void checkBox_RecordMUA_CheckedChanged(object sender, EventArgs e)
        {
            // Set recording parameters
            ResetStreams2Record();
            SettingsHaveChanged(this, e);
        }

        private void checkBox_RecordStim_CheckedChanged(object sender, EventArgs e)
        {
            // Set recording parameters
            ResetStreams2Record();
            SettingsHaveChanged(this, e);
        }

        private void checkBox_RecaordAuxAnalog_CheckedChanged(object sender, EventArgs e)
        {
            // Set recording parameters
            ResetStreams2Record();
            SettingsHaveChanged(this, e);
        }

        private void checkBox_RecordAuxDig_CheckedChanged(object sender, EventArgs e)
        {
            // Set recording parameters
            ResetStreams2Record();
            SettingsHaveChanged(this, e);
        }

        private void checkBox_RecordSpikeData_CheckedChanged(object sender, EventArgs e)
        {
            // Set recording parameters
            ResetStreams2Record();
            SettingsHaveChanged(this, e);
        }

        private void ResetStreams2Record()
        {
            // Set recording parameters
            recordSpike = checkBox_RecordSpikeData.Checked;
            recordRaw = checkBox_RecordRaw.Checked;
            recordSALPA = checkBox_RecordSALPA.Checked;
            recordSpikeFilt = checkBox_RecordSpikeFilt.Checked;
            recordLFP = checkBox_RecordLFP.Checked;
            recordEEG = checkBox_RecordEEG.Checked;
            recordMUA = checkBox_RecordMUA.Checked;
            recordStim = checkBox_RecordStim.Checked;
            recordAuxDig = checkBox_RecordAuxDig.Checked;
            recordAuxAnalog = checkBox_RecordAuxAnalog.Checked;
        }

        private void button_MakeRawSelections_Click(object sender, EventArgs e)
        {
            this.Hide();
        }


    }
}