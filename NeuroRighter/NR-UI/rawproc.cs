﻿// EPHYSWRITE.CS
// Copyright (c) 2008-2009 John Rolston
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
using rawType = System.Double;
using NeuroRighter.SpikeDetection;
using NeuroRighter.DataTypes;
using ExtensionMethods;


namespace NeuroRighter
{
    ///<summary>Methods for processing raw data streams. This includes filtering (bandpass and SALPA) and the creation of EEG,LFP and MUA data streams
    ///and, if appropriate, sending those raw streams to file.</summary>
    ///<author>John Rolston</author>
    sealed internal partial class NeuroRighter : Form
    {
        private void bwSpikes_DoWork(object sender, DoWorkEventArgs e)
        {
            bool test = Properties.Settings.Default.recordSpikes;
            Object[] state = (Object[])e.Argument;
            int taskNumber = (int)state[0];
            //Debugger.Write(taskNumber.ToString() + ": dowork begin");
            trackingProc[taskNumber]++;
            //double[][] filtSpikeData;
            //Copy data into a new buffer
            for (int i = 0; i < numChannelsPerDev; ++i)
                spikeData[taskNumber][i].GetRawData(0, spikeBufferLength, filtSpikeData[taskNumber * numChannelsPerDev + i], 0);
            //Debugger.Write(taskNumber.ToString() + ": raw data read");
            //Account for Pre-amp gain
            double ampdec = (1 / Properties.Settings.Default.PreAmpGain);
            for (int i = taskNumber * numChannelsPerDev; i < (taskNumber + 1) * numChannelsPerDev; ++i)
                for (int j = 0; j < spikeBufferLength; ++j)
                    filtSpikeData[i][j] = ampdec * filtSpikeData[i][j];

            // Send filtSpikeData to datSrv
            datSrv.rawElectrodeSrv.WriteToBuffer(filtSpikeData, taskNumber, numChannelsPerDev);
          //  Debugger.Write(taskNumber.ToString() + ": raw data sent to rawsrv");
            #region Write RAW data
            //Write data to file

            if (switch_record.Value && recordingSettings.recordRaw && spikeTask != null)
            {

                lock (recordingSettings.rawOut)
                {

                    for (int i = taskNumber * numChannelsPerDev; i < (taskNumber + 1) * numChannelsPerDev; ++i)
                    {
                        // Temporary storage for converte data
                        Int16[] tempBuff;

                        // Convert raw data to 16-bit int
                        tempBuff = neuralDataScaler.ConvertSoftRawRowToInt16(ref filtSpikeData[i]);

                        // Send data to file writer
                        for (int j = 0; j < spikeBufferLength; ++j)
                        {
                            recordingSettings.rawOut.read(tempBuff[j], i);
                        }
                    }
                }
            }

            #endregion

           // Debugger.Write(taskNumber.ToString() + ": raw written");

            #region LFP_Filtering
            //Filter for LFPs
            if (!Properties.Settings.Default.SeparateLFPBoard && Properties.Settings.Default.UseLFPs)
            {
                //Copy to new array
                for (int i = taskNumber * numChannelsPerDev; i < (taskNumber + 1) * numChannelsPerDev; ++i)
                    for (int j = 0; j < spikeBufferLength; ++j)
                        filtLFPData[i][j] = filtSpikeData[i][j];

                #region ArtiFilt (interpolation filtering)
                if (checkBox_artiFilt.Checked)
                    artiFilt.filter(ref filtLFPData, stimIndices, taskNumber * numChannelsPerDev, numChannelsPerDev, numStimReads[taskNumber] - 1);
                #endregion

                if (checkBox_LFPsFilter.Checked)
                    for (int i = taskNumber * numChannelsPerDev; i < (taskNumber + 1) * numChannelsPerDev; ++i)
                        lfpFilter[i].filterData(filtLFPData[i]);

                //Downsample for LFPs
                double dsFactor = (double)spikeSamplingRate / (double)lfpSamplingRate;
                if (dsFactor % 1 == 0) //If it's an integer
                {
                    for (int i = taskNumber * numChannelsPerDev; i < (taskNumber + 1) * numChannelsPerDev; ++i)
                        for (int j = 0; j < lfpBufferLength; ++j)
                            finalLFPData[i][j] = filtLFPData[i][(int)(dsFactor * j)];
                }
                else
                {
                    for (int i = taskNumber * numChannelsPerDev; i < (taskNumber + 1) * numChannelsPerDev; ++i)
                        for (int j = 0; j < lfpBufferLength; ++j)
                            finalLFPData[i][j] = filtLFPData[i][(int)(Math.Round(dsFactor * j))];
                }

                //Do IISZapper stuff
                if (IISDetected != null) IISDetected(this, finalLFPData, numSpikeReads[taskNumber]);

                // Send to datSrv
                datSrv.lfpSrv.WriteToBuffer(finalLFPData, 0, numChannels);

                #region WriteLFPFile
                if (switch_record.Value && recordingSettings.recordLFP && spikeTask != null) //Convert to 16-bit ints, then write to file
                {
                    for (int i = taskNumber * numChannelsPerDev; i < (taskNumber + 1) * numChannelsPerDev; ++i)
                    {
                        // Temporary storage for converte data
                        Int16[] tempLFPBuff;

                        // Convert raw data to 16-bit int
                        tempLFPBuff = neuralDataScaler.ConvertSoftRawRowToInt16(ref finalLFPData[i]);

                        // Send data to file writer
                        for (int j = 0; j < lfpBufferLength; ++j)
                        {
                            recordingSettings.lfpOut.read((short)tempLFPBuff[j], i);
                        }
                    }

                }
                #endregion

                //Digital ref LFP signals
                if (!checkBox_digRefLFPs.Checked) { /* Do nothing, since prefetch makes if faster than else */ }
                else
                {
                    int refChan = Convert.ToInt16(numericUpDown_digRefLFPs.Value) - 1;
                    for (int i = 0; i < refChan; ++i)
                        for (int j = 0; j < lfpBufferLength; ++j)
                            finalLFPData[i][j] -= finalLFPData[refChan][j];
                    for (int i = refChan + 1; i < numChannels; ++i)
                        for (int j = 0; j < lfpBufferLength; ++j)
                            finalLFPData[i][j] -= finalLFPData[refChan][j];
                }

                //Post to PlotData buffer
                lfpPlotData.write(finalLFPData, taskNumber * numChannelsPerDev, numChannelsPerDev);
            }
            #endregion

           // Debugger.Write(taskNumber.ToString() + ": lfp filtered");

            #region SALPA Filtering
            lock (stimIndices)
                if (checkBox_SALPA.Checked && numStimReads == null) //Account for those not using the stimulator and stimulus coding scheme
                {
                    SALPAFilter.filter(ref filtSpikeData, taskNumber * numChannelsPerDev, numChannelsPerDev, stimIndices, 0);

                    // Send filtSpikeData to datSrv
                    datSrv.salpaElectrodeSrv.WriteToBuffer(filtSpikeData, taskNumber, numChannelsPerDev);

                }
                else if (checkBox_SALPA.Checked)
                {
                    SALPAFilter.filter(ref filtSpikeData, taskNumber * numChannelsPerDev, numChannelsPerDev, stimIndices, numStimReads[taskNumber] - 1);

                    // Send filtSpikeData to datSrv
                    datSrv.salpaElectrodeSrv.WriteToBuffer(filtSpikeData, taskNumber, numChannelsPerDev);

                }

            if (switch_record.Value && recordingSettings.recordSALPA && spikeTask != null)
            {
                lock (recordingSettings.salpaOut)
                {
                    int startIdx;

                    if (firstRawWrite[taskNumber]) // account for SALPA delay
                    {
                        if (!recordingSettings.recordSpikeFilt)
                            firstRawWrite[taskNumber] = false;
                        startIdx = SALPAFilter.offset();
                    }
                    else
                    {
                        startIdx = 0;
                    }

                    for (int i = taskNumber * numChannelsPerDev; i < (taskNumber + 1) * numChannelsPerDev; ++i)
                    {
                        // Temporary storage for converte data
                        Int16[] tempBuff;

                        // Convert raw data to 16-bit int
                        tempBuff = neuralDataScaler.ConvertSoftRawRowToInt16(ref filtSpikeData[i]);

                        // Send data to file writer
                        for (int j = startIdx; j < spikeBufferLength; ++j)
                        {
                            recordingSettings.salpaOut.read((short)tempBuff[j], i);
                        }
                    }
                }
            }

            #endregion SALPA Filtering

          //  Debugger.Write(taskNumber.ToString() + ": salpa filtered");

            #region SpikeFiltering
            //Filter spike data
            if (checkBox_spikesFilter.Checked)
            {

                // Filter data
                for (int i = numChannelsPerDev * taskNumber; i < numChannelsPerDev * (taskNumber + 1); ++i)
                    spikeFilter[i].filterData(filtSpikeData[i]);

                // Send filtSpikeData to datSrv
                datSrv.filteredElectrodeSrv.WriteToBuffer(filtSpikeData, taskNumber, numChannelsPerDev);



                if (switch_record.Value && recordingSettings.recordSpikeFilt && (spikeTask != null))
                {
                    lock (recordingSettings.spkFiltOut)
                    {

                        int startIdx;
                        if (firstRawWrite[taskNumber] && checkBox_SALPA.Checked) // account for SALPA delay
                        {
                            firstRawWrite[taskNumber] = false;
                            startIdx = SALPAFilter.offset();
                        }
                        else
                        {
                            startIdx = 0;
                        }

                        for (int i = taskNumber * numChannelsPerDev; i < (taskNumber + 1) * numChannelsPerDev; ++i)
                        {
                            // Temporary storage for converte data
                            Int16[] tempBuff;

                            // Convert raw data to 16-bit int
                            tempBuff = neuralDataScaler.ConvertSoftRawRowToInt16(ref filtSpikeData[i]);

                            // Send data to file writer
                            for (int j = startIdx; j < spikeBufferLength; ++j)
                            {
                                recordingSettings.spkFiltOut.read((short)tempBuff[j], i);
                            }
                        }
                    }
                }


            }
            #endregion

          //  Debugger.Write(taskNumber.ToString() + ": spike filtered");

            //NEED TO FIX FOR MULTI DEVS
            #region Digital_Referencing_Spikes
            //Digital ref spikes signals
            if (checkBox_digRefSpikes.Checked)
            {
                int refChan = Convert.ToInt16(numericUpDown_digRefSpikes.Value) - 1;
                for (int i = 0; i < refChan; ++i)
                    for (int j = 0; j < spikeBufferLength; ++j)
                        filtSpikeData[i][j] -= filtSpikeData[refChan][j];
                for (int i = refChan + 1; i < numChannels; ++i)
                    for (int j = 0; j < spikeBufferLength; ++j)
                        filtSpikeData[i][j] -= filtSpikeData[refChan][j];
            }

            //Common average or median referencing
            if (referncer != null)
            {
                lock (referncer)
                    referncer.reference(filtSpikeData, taskNumber * numChannelsPerDev, numChannelsPerDev);
            }
            #endregion

         //   Debugger.Write(taskNumber.ToString() + ": digital referencing spikes");

            #region SpikeDetection
            ++(numSpikeReads[taskNumber]);

            SALPA_WIDTH = Convert.ToInt32(numericUpDown_salpa_halfwidth.Value);

            ulong startTime = (ulong)(numSpikeReads[taskNumber] - 1) * (ulong)spikeBufferLength; //Used to mark spike time for *.spk file


            //newWaveforms: 0 based indexing for internal NR processing (datSrv, plotData)
            EventBuffer<SpikeEvent> newWaveforms = new EventBuffer<SpikeEvent>(Properties.Settings.Default.RawSampleFrequency);
            switch (spikeDet.detectorType)
            {
                case 0:
                    for (int i = taskNumber * numChannelsPerDev; i < (taskNumber + 1) * numChannelsPerDev; ++i)
                        newWaveforms.eventBuffer.AddRange(spikeDet.spikeDetector.DetectSpikes(filtSpikeData[i], i, startTime));
                    break;
                case 1:
                    for (int i = taskNumber * numChannelsPerDev; i < (taskNumber + 1) * numChannelsPerDev; ++i)
                        newWaveforms.eventBuffer.AddRange(spikeDet.spikeDetector.DetectSpikesSimple(filtSpikeData[i], i, startTime));
                    break;
            }

            //Extract waveforms
            //toRawsrv: 0 index, includes timing offsets, channel remapping
            //saved: 1 index
            test = Properties.Settings.Default.recordSpikes;
            EventBuffer<SpikeEvent> toRawsrv = new EventBuffer<SpikeEvent>(spikeSamplingRate);
            if (Properties.Settings.Default.ChannelMapping != "invitro" || numChannels != 64) //check this first, so we don't have to check it for each spike
            {
                for (int j = 0; j < newWaveforms.eventBuffer.Count; ++j) //For each threshold crossing
                {
                    SpikeEvent tmp = (SpikeEvent)newWaveforms.eventBuffer[j].DeepClone();
                    if (checkBox_SALPA.Checked)
                        if (tmp.sampleIndex >= (ulong)SALPAFilter.offset())
                            tmp.sampleIndex -= (ulong)SALPAFilter.offset(); //To account for delay of SALPA filter
                        else
                            continue; //skip that one

                    toRawsrv.eventBuffer.Add(tmp);
                }
            }
            else //in vitro mappings
            {
                test = Properties.Settings.Default.recordSpikes;
                for (int j = 0; j < newWaveforms.eventBuffer.Count; ++j) //For each threshold crossing
                {
                    SpikeEvent tmp = (SpikeEvent)newWaveforms.eventBuffer[j].DeepClone();

                    if (checkBox_SALPA.Checked)
                        if (tmp.sampleIndex >= (ulong)SALPAFilter.offset())
                            tmp.sampleIndex -= (ulong)SALPAFilter.offset(); //To account for delay of SALPA filter
                        else
                            continue; //skip that one

                    tmp.channel = MEAChannelMappings.channel2LinearCR(tmp.channel);
                    toRawsrv.eventBuffer.Add(tmp);
                }
            }

            // Spike Detection - Hoarding
            if (spikeDet.isHoarding)
            {
                // Send spikes to the sorter's internal buffer
                spikeDet.spikeSorter.HoardSpikes(toRawsrv);
                spikeDet.UpdateCollectionBar();
            }

            // Spike Detection - Classification
            if (spikeDet.isEngaged)
            {
                spikeDet.spikeSorter.Classify(ref toRawsrv);
            }

            // Provide new spike data to persistent buffer
            datSrv.spikeSrv.WriteToBuffer(toRawsrv, taskNumber);

            // Record spike waveforms 
            if (switch_record.Value && Properties.Settings.Default.recordSpikes)
            {
                for (int j = 0; j < newWaveforms.eventBuffer.Count; ++j) //For each threshold crossing
                {
                    SpikeEvent tmp = toRawsrv.eventBuffer[j];

                    lock (recordingSettings.spkOut) //Lock so another NI card doesn't try writing at the same time
                    {
                        recordingSettings.spkOut.WriteSpikeToFile((short)((int)tmp.channel + (int)CHAN_INDEX_START), (int)tmp.sampleIndex,
                            tmp.threshold, tmp.waveform, tmp.unit);
                    }
                }
            }


            //Post to PlotData
            if (spikeDet.isEngaged)
            {
                waveformPlotData.write(toRawsrv.eventBuffer, spikeDet.spikeSorter.unitDictionary);
            }
            else
            {
                waveformPlotData.write(toRawsrv.eventBuffer, null);
            }
            //Clear new ones, since we're done with them.
            newWaveforms.eventBuffer.Clear();
            #endregion

          //  Debugger.Write(taskNumber.ToString() + ": spikes detected");

            #region BNC_Output
            //Send selected channel to BNC
            if (Properties.Settings.Default.UseSingleChannelPlayback)
            {
                int ch = (int)(channelOut.Value) - 1;
                if (ch >= numChannelsPerDev * taskNumber && ch < numChannelsPerDev * (taskNumber + 1))
                    //spikeOutWriter.BeginWriteMultiSample(true, filtSpikeData[ch], null, null);
                    BNCOutput.write(filtSpikeData[ch]);
                //spikeOutWriter.WriteMultiSample(true, filtSpikeData[ch - 1]);
            }
            #endregion

         //   Debugger.Write(taskNumber.ToString() + ": bnc output");
            //Write to PlotData buffer
            spikePlotData.write(filtSpikeData, taskNumber * numChannelsPerDev, numChannelsPerDev);
         //   Debugger.Write(taskNumber.ToString() + ": spikes plotted");
            #region MUA
            if (Properties.Settings.Default.ProcessMUA)
            {
                muaFilter.Filter(filtSpikeData, taskNumber * numChannelsPerDev, numChannelsPerDev, ref muaData);

                //Write to plot buffer
                muaPlotData.write(muaData, taskNumber * numChannelsPerDev, numChannelsPerDev);
            }
            #endregion
        //    Debugger.Write(taskNumber.ToString() + ": multi unit activity done/processing done");
            bwIsRunning[taskNumber] = false;
            e.Result = taskNumber;
        }

        internal delegate void spikesAcquiredHandler(object sender, bool inTrigger);

        internal event spikesAcquiredHandler spikesAcquired;

        private void bwSpikes_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            int taskNumber = (int)e.Result;

            //Check whether timed recording is done or stopped
            if (checkBox_enableTimedRecording.Checked && DateTime.Now > timedRecordingStopTime)
            {
                if (taskNumber == spikeReader.Count - 1)
                {
                    if (checkbox_repeatRecord.Checked)//make sure this is the last spike processor so that this only gets called once  -&& (((int)e.Result)==(bwSpikes.Count-1))
                    {
                        buttonStop_Click(null, null);
                        reset();
                        buttonStart_Click(null, null);
                    }
                    else
                    {
                        buttonStop_Click(null, null);
                        //buttonStop.PerformClick();
                    }
                }
            }
            else
            {
                try
                {
                    spikeReader[taskNumber].BeginMemoryOptimizedReadWaveform(spikeBufferLength, spikeCallback, taskNumber, spikeData[taskNumber]);
                }
                catch (DaqException exception)
                {
                    Console.WriteLine("DaqException caused reset while attempting to read from task " + taskNumber.ToString() + ": " + exception.Message);
                    reset();
                }
            }
        }

    }
}
