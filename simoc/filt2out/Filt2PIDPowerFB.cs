﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using simoc.UI;
using simoc.srv;
using simoc.persistantstate;
using NeuroRighter.StimSrv;
using NeuroRighter.DataTypes;

namespace simoc.filt2out
{
    class Filt2PIDPowerFB : Filt2Out
    {
        ulong pulseWidthSamples;
        double offVoltage = -0.5;
        double K;
        double Ti;
        double Td;
        double currentFilteredValue;
        double stimFreqHz;
        double stimPulseWidthMSec;
        double currentTargetInt;
        double lastErrorInt;
        double N = 2;

        public Filt2PIDPowerFB(ref NRStimSrv stimSrv, ControlPanel cp)
            : base(ref stimSrv, cp)
        {
            numberOutStreams = 3; // P, I and D streams
            K = c0;
            if (c1 != 0)
                Ti = 1 / c1;
            else
                Ti = 0;
            Td = c2;
            stimFreqHz = c3;
            stimPulseWidthMSec = c4;
        }

        internal override void CalculateError(ref double currentError, double currentTarget, double currentFilt)
        {
            currentFilteredValue = currentFilt;
            base.CalculateError(ref currentError, currentTarget, currentFilt);
            if (currentTarget != 0)
            {
                lastErrorInt = currentError;
                currentError = (currentTarget - currentFilt);  // currentTarget;
            }
            else
            {
                lastErrorInt = currentError;
                currentError = 0;
            }
            currentErrorInt = currentError;
            currentTargetInt = currentTarget;
        }


        internal override void SendFeedBack(PersistentSimocVar simocVariableStorage)
        {
            base.SendFeedBack(simocVariableStorage);

            simocVariableStorage.LastErrorValue = lastErrorInt;

            // Generate output frequency\
            if (currentTargetInt != 0)
            {
                // Derivative Approx
                simocVariableStorage.GenericDouble3 = (Td / (Td + N * stimSrv.DACPollingPeriodSec)) * simocVariableStorage.GenericDouble3 -
                    (K*Td*N)/(Td + N * stimSrv.DACPollingPeriodSec) * (currentFilteredValue - simocVariableStorage.LastFilteredObs);
                
                // Tustin's Integral approximation
                simocVariableStorage.GenericDouble2 += K * Ti * stimSrv.DACPollingPeriodSec * currentErrorInt;
                
                // PID feedback signal
                simocVariableStorage.GenericDouble1 = K * currentErrorInt + simocVariableStorage.GenericDouble2 + simocVariableStorage.GenericDouble3;
            }
            else
            {
                simocVariableStorage.GenericDouble3 = 0;
                simocVariableStorage.GenericDouble2 = 0;
                simocVariableStorage.GenericDouble1 = 0;
            }

            // Set upper and lower bounds
            if (simocVariableStorage.GenericDouble1 < 0)
                simocVariableStorage.GenericDouble1 = 0;
            if (simocVariableStorage.GenericDouble1 > 5)
                simocVariableStorage.GenericDouble1 = 5;

            // set the currentFeedback array
            currentFeedbackSignals = new double[numberOutStreams];
            currentFeedbackSignals[0] = simocVariableStorage.GenericDouble1;

            // Put I and D error components in the rest of the currentFeedBack array
            currentFeedbackSignals[1] = simocVariableStorage.GenericDouble2;
            currentFeedbackSignals[2] = simocVariableStorage.GenericDouble3;

            // Get the pulse width (msec)
            pulseWidthSamples = (ulong)(stimSrv.sampleFrequencyHz * stimPulseWidthMSec / 1000);

            // Create the output buffer
            List<AuxOutEvent> toAppendAux = new List<AuxOutEvent>();
            List<DigitalOutEvent> toAppendDig = new List<DigitalOutEvent>();
            ulong isi = (ulong)(hardwareSampFreqHz / stimFreqHz);

            // Get the current buffer sample and make sure that we are going
            // to produce stimuli that are in the future
            if (simocVariableStorage.NextAuxEventSample < nextAvailableSample)
            {
                simocVariableStorage.NextAuxEventSample = nextAvailableSample;
            }

            // Make periodic stimulation
            while (simocVariableStorage.NextAuxEventSample <= (nextAvailableSample + (ulong)stimSrv.GetBuffSize()))
            {
                // Send a V_ctl = simocVariableStorage.GenericDouble1 volt pulse to channel 0 for c2 milliseconds.
                toAppendAux.Add(new AuxOutEvent((ulong)(simocVariableStorage.NextAuxEventSample + loadOffset), 0, simocVariableStorage.GenericDouble1));
                toAppendAux.Add(new AuxOutEvent((ulong)(simocVariableStorage.NextAuxEventSample + loadOffset) + pulseWidthSamples, 0, offVoltage));

                // Encode light power as 10000*V_ctl = port-state
                toAppendDig.Add(new DigitalOutEvent((ulong)(simocVariableStorage.NextAuxEventSample + loadOffset), (uint)(10000.0 * simocVariableStorage.GenericDouble1)));

                simocVariableStorage.LastAuxEventSample = simocVariableStorage.NextAuxEventSample;
                simocVariableStorage.NextAuxEventSample += isi;
            }


            // Send to bit 0 of the digital output port
            SendAuxAnalogOutput(toAppendAux);
            SendAuxDigitalOutput(toAppendDig);

        }
    }
}