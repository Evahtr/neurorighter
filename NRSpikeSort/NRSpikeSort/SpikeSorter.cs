﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NeuroRighter.DataTypes;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;

namespace NRSpikeSort
{
    [Serializable()]
    public class SpikeSorter : ISerializable
    {
        /// <summary>
        /// Says whether the classifiers have been trained yet.
        /// </summary>
        public bool trained;

        /// <summary>
        /// List of Gaussian Mixture models used to classify spikes. One for each electrode.
        /// </summary>
        public List<ChannelModel> channelModels;

        /// <summary>
        /// The data used to train the spike sorter.
        /// </summary>
        public EventBuffer<SpikeEvent> trainingSpikes = new EventBuffer<SpikeEvent>(25000);

        /// <summary>
        /// The total number of units found on the electrode array
        /// </summary>
        public int totalNumberOfUnits = 0;

        /// <summary>
        /// This hash allows one to switch back and forth between the absolute unit number 
        /// and the unit number for a given channel. (e.g. unit 78 => unit 2 on chan 28).
        /// </summary>
        public Hashtable unitDictionary;

        /// <summary>
        /// Maximum number of units that could be detected per channel.
        /// </summary>
        public int maxK; // maximum number of subclasses

        /// <summary>
        /// Minimum number of training spikes collected on a channel to creat a sorter for 
        /// that channel.
        /// </summary>
        public int minSpikes; // minimum number of spikes required to attempt sorting

        /// <summary>
        /// The channels to be sorted
        /// </summary>
        public List<int> channelsToSort; // The channels that had enough data to sort

        /// <summary>
        /// Number of spikes detected for training on each channel.
        /// </summary>
        public Hashtable spikesCollectedPerChannel;
        
        // Private
        private int numberChannels;
        private int inflectionSample; // The sample that spike peaks occur at
        private const int maxTrainingSpikesPerChannel = 50;
        
        /// <summary>
        /// NeuroRighter's spike sorter.
        /// </summary>
        /// <param name="numberChannels">Number of channels to make sorters for</param>
        /// <param name="maxK">Maximum number of units to consider per channel</param>
        /// <param name="minSpikes">Minimum number of detected training spikes to create a sorter for a given channel</param>
        public SpikeSorter(int numberChannels, int maxK, int minSpikes)
        {
            this.numberChannels = numberChannels;
            this.maxK = maxK;
            this.minSpikes = minSpikes;
            this.channelModels = new List<ChannelModel>(numberChannels);
            this.spikesCollectedPerChannel = new Hashtable();
            for (int i = 0; i < numberChannels; ++i)
            {
                spikesCollectedPerChannel.Add(i+1, 0);
            }

        }

        /// <summary>
        /// Hoard spikes to populate the buffer of spikes that will be used to train the classifiers
        /// </summary>
        /// <param name="newSpikes"> An EventBuffer contain spikes to add to the training buffer</param>
        public void HoardSpikes(EventBuffer<SpikeEvent> newSpikes)
        {
            for (int i = 0; i < newSpikes.eventBuffer.Count; ++i)
            {
                if (!((int)spikesCollectedPerChannel[newSpikes.eventBuffer[i].channel+1] >= maxTrainingSpikesPerChannel))
                {
                    spikesCollectedPerChannel[newSpikes.eventBuffer[i].channel+1] = (int)spikesCollectedPerChannel[newSpikes.eventBuffer[i].channel+1] + 1;
                    trainingSpikes.eventBuffer.Add(newSpikes.eventBuffer[i]);
                }
            }
        }

        /// <summary>
        /// Trains a classifier for each channel so long as (int)minSpikes worth of spikes have been collected
        /// for that channel in the training buffer
        /// </summary>
        /// <param name="peakSample"> The sample within a spike snippet that corresponds to the aligned peak.</param>
        public void Train(int peakSample)
        {
            // Clear old channel models
            channelsToSort = new List<int>();
            channelModels.Clear();

            // Clear old unit dictionary
            unitDictionary = new Hashtable();

            // Add the zero unit to the dictionary
            unitDictionary.Add(0,0);

            // Set the inflection sample
            inflectionSample = peakSample;

            //// Clear the spikesPerChannel hash
            //spikesCollectedPerChannel.Clear();
            //for (int i = 0; i < numberChannels; ++i)
            //{
            //    spikesCollectedPerChannel.Add(i, 0);
            //}

            // Make sure we have something in the training matrix
            if (trainingSpikes.eventBuffer.Count == 0)
            {
                throw new InvalidOperationException("The training data set was empty");
            }

            for (int i = 0; i < numberChannels; ++i)
            {
                // Current channel
                int currentChannel = i;

                // Get the spikes that belong to this channel
                List<SpikeEvent> spikesOnChan = trainingSpikes.eventBuffer.Where(x => x.channel == currentChannel).ToList();

                // Project channel data
                if (spikesOnChan.Count >= minSpikes)
                {
                    // Note that we have to sort spikes on this channel
                    channelsToSort.Add(currentChannel);

                    // Train a channel model for this channel
                    ChannelModel thisChannelModel = new ChannelModel(currentChannel, maxK, totalNumberOfUnits);

                    // Project Data
                    thisChannelModel.MaxInflectProject(spikesOnChan.ToList(), inflectionSample);

                    // Train Classifier
                    thisChannelModel.Train();

                    // Update the unit dicationary and increment the total number of units
                    for (int k = 1; k <= thisChannelModel.K; ++k)
                    {
                        unitDictionary.Add(totalNumberOfUnits + k, k);
                    }

                    totalNumberOfUnits += thisChannelModel.K;

                    // Add the channel model to the list
                    channelModels.Add(thisChannelModel);
                }
            }

            // All finished
            trained = true;
        }

        /// <summary>
        /// After the channel models (gmm's) have been created and trained, this function
        /// is used to classifty newly detected spikes for which a valide channel model exisits.
        /// </summary>
        /// <param name="newSpikes"> An EventBuffer conataining spikes to be classified</param>
        public void Classify(ref EventBuffer<SpikeEvent> newSpikes)
        {

            // Make sure the channel models are trained.
            if (!trained)
            {
                throw new InvalidOperationException("The channel models were not yet trained so classification is not possible.");
            }

            // Sort the channels that need sorting
            for (int i = 0; i < channelsToSort.Count; ++i)
            {
                // Current channel
                int currentChannel = channelsToSort[i];

                // Get the spikes that belong to this channel ///////TODO: will we maintain the reference through this??
                List<SpikeEvent> spikesOnChan = newSpikes.eventBuffer.Where(x => x.channel == currentChannel).ToList();

                // If there are no spikes on this channel
                if (spikesOnChan.Count == 0)
                    continue;

                // Get the channel model for this channel
                ChannelModel thisChannelModel = channelModels[channelsToSort.IndexOf(currentChannel)];

                // Project the spikes
                //thisChannelModel.PCProject(spikesOnChan.ToList());
                thisChannelModel.MaxInflectProject(spikesOnChan.ToList(), 14);

                // Sort the spikes
                thisChannelModel.Classify();

                // Update the newSpikes buffer 
                for (int j = 0; j < spikesOnChan.Count; ++j)
                {
                    spikesOnChan[j].SetUnit(thisChannelModel.classes[j] + thisChannelModel.unitStartIndex + 1);
                }

            }

        }

        #region Serialization Constructors/Deconstructors
        public SpikeSorter(SerializationInfo info, StreamingContext ctxt)
        {
            this.trained = (bool)info.GetValue("trained", typeof(bool));
            this.numberChannels = (int)info.GetValue("numberChannels",typeof(int));
            this.minSpikes = (int)info.GetValue("minSpikes", typeof(int));
            this.maxK = (int)info.GetValue("maxK", typeof(int));
            this.inflectionSample = (int)info.GetValue("inflectionSample", typeof(int));
            this.trainingSpikes = (EventBuffer<SpikeEvent>)info.GetValue("trainingSpikes", typeof(EventBuffer<SpikeEvent>));
            this.channelsToSort = (List<int>)info.GetValue("channelsToSort", typeof(List<int>));
            this.channelModels = (List<ChannelModel>)info.GetValue("channelModels", typeof(List<ChannelModel>));
            this.unitDictionary = (Hashtable)info.GetValue("unitDictionary", typeof(Hashtable));
        }

        public void GetObjectData(SerializationInfo info, StreamingContext ctxt)
        {
            info.AddValue("trained", this.trained);
            info.AddValue("numberChannels", this.numberChannels);
            info.AddValue("minSpikes", this.minSpikes);
            info.AddValue("maxK", this.maxK);
            info.AddValue("inflectionSample", this.inflectionSample);
            info.AddValue("trainingSpikes", this.trainingSpikes);
            info.AddValue("channelsToSort", this.channelsToSort);
            info.AddValue("channelModels", this.channelModels);
            info.AddValue("unitDictionary", this.unitDictionary);
        }

        #endregion

    }
}
