﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Diagnostics;
//using Microsoft.Kinect;
using System.Windows.Media.Media3D;
using Vrpn;
using KinectBase;

namespace KinectWithVRServer
{
    class ServerCore
    {
        /// <summary>
        /// Flag used to indicate whether we've entered the main loop, and modified to stop the loop.
        /// Probably a race condition - you probably want to either use like a semaphore, or at least atomic data structures.
        /// </summary>
        private volatile bool forceStop = false;
        private volatile ServerRunState serverState = ServerRunState.Stopped;  //Only modify this inside a lock on the runningLock object!
        public ServerRunState ServerState
        {
            get { return serverState; }
        }
        public bool isRunning
        {
            get { return (serverState == ServerRunState.Running); }
        }
        Vrpn.Connection vrpnConnection;
        internal KinectBase.MasterSettings serverMasterOptions;
        internal List<Vrpn.ButtonServer> buttonServers;
        internal List<Vrpn.AnalogServer> analogServers;
        internal List<Vrpn.TextSender> textServers;
        internal List<Vrpn.TrackerServer> trackerServers;

        private List<KinectSkeletonsData> perKinectSkeletons = new List<KinectSkeletonsData>();
        private List<MergedSkeleton> mergedSkeletons = new List<MergedSkeleton>();
        private System.Timers.Timer skeletonUpdateTimer;  //Use a timer to update the skeletons at a constant rate since we might have so much data coming in that updating them as we get data would just clog the network
        bool verbose = false;
        bool GUI = false;
        public bool Verbose
        {
            get { return verbose; }
            set
            {
                verbose = value;
                if (voiceRecog != null)
                {
                    voiceRecog.verbose = value;
                }
                if (feedbackCore != null)
                {
                    feedbackCore.isVerbose = value;
                }
            }
        }
        MainWindow parent;
        internal List<KinectBase.IKinectCore> kinects = new List<KinectBase.IKinectCore>();
        VoiceRecogCore voiceRecog;
        FeedbackCore feedbackCore;
        internal Point3D? feedbackPosition = null;

        public ServerCore(bool isVerbose, KinectBase.MasterSettings serverOptions, MainWindow guiParent = null)
        {                
            parent = guiParent;
            verbose = isVerbose;
            serverMasterOptions = serverOptions;

            if (guiParent != null)
            {
                GUI = true;
            }
        }

        public void launchServer()
        {
            //These don't need a lock to be thread safe since they are volatile
            forceStop = false;
            serverState = ServerRunState.Starting;

            string errorMessage = "";
            if (parseSettings(out errorMessage))
            {
                //Start the Kinect audio streams and create the per Kinect skeleton lists
                for (int i = 0; i < kinects.Count; i++)
                {
                    if (kinects[i].version == KinectVersion.KinectV1)
                    {
                        ((KinectV1Wrapper.Core)kinects[i]).StartKinectAudio(); 
                    }
                    else if (kinects[i].version == KinectVersion.KinectV2)
                    {
                        //TODO: Start Kinect v2 audio
                    }
                }

                //Start the feedback client if necessary
                if (serverMasterOptions.feedbackOptions.useFeedback)
                {
                    feedbackCore = new FeedbackCore(verbose, this, parent);
                    feedbackCore.StartFeedbackCore(serverMasterOptions.feedbackOptions.feedbackServerName, serverMasterOptions.feedbackOptions.feedbackSensorNumber);
                }

                runServerCoreDelegate serverDelegate = runServerCore;
                serverDelegate.BeginInvoke(null, null);

                //Start voice recognition, if necessary
                if (serverMasterOptions.voiceCommands.Count > 0)
                {
                    voiceRecog = new VoiceRecogCore(this, verbose, parent);
                    launchVoiceRecognizerDelegate voiceDelegate = voiceRecog.launchVoiceRecognizer;
                    //Dispatcher newDispatch = new Dispatcher();

                    voiceDelegate.BeginInvoke(new AsyncCallback(voiceStartedCallback), null);
                    //voiceRecog.launchVoiceRecognizer();
                }
                else
                {
                    //Because the voice callback will not be called, we need to call this stuff here
                    if (GUI)
                    {
                        parent.startServerButton.Content = "Stop";
                        parent.startServerButton.IsEnabled = true;
                        parent.ServerStatusItem.Content = "Running";
                        parent.ServerStatusTextBlock.Text = "Running";
                    }
                }
            }
            else
            {
                HelperMethods.ShowErrorMessage("Error", "Settings parsing failed!  See the log for more details.", parent);
                HelperMethods.WriteToLog(errorMessage, parent);
            }
        }

        private void voiceStartedCallback(IAsyncResult ar)
        {
            HelperMethods.WriteToLog("Voice started!", parent);

            if (GUI)
            {
                parent.Dispatcher.BeginInvoke((Action)(() =>
                {
                    parent.startServerButton.Content = "Stop";
                    parent.startServerButton.IsEnabled = true;
                    parent.ServerStatusItem.Content = "Running";
                    parent.ServerStatusTextBlock.Text = "Running";
                }), null
                );
            }
        }

        /// <summary>
        /// Stops the server from running, but does not cleanup all the settings in case you want to turn the server back on
        /// </summary>
        public void stopServer()
        {
            forceStop = true ;

            if (voiceRecog != null)
            {
                voiceRecog.stopVoiceRecognizer();
            }

            if (feedbackCore != null)
            {
                feedbackCore.StopFeedbackCore();
            }

            int count = 0;
            while (count < 30)
            {
                if (serverState == ServerRunState.Stopped)
                {
                    break;
                }
                count++;
                Thread.Sleep(100);
            }
            if (count >= 30 && serverState != ServerRunState.Stopped)
            {
                throw new Exception("VRPN server shutdown failed!");
            }
        }

        private static void updateList<T>(ref List<T> serverlist) where T : Vrpn.IVrpnObject
        {
            for (int i = 0; i < serverlist.Count; i++)
            {
                lock (serverlist[i])
                {
                    serverlist[i].Update();
                }
            }
        }

        private static void disposeList<T>(ref List<T> list) where T : IDisposable
        {
            for (int i = 0; i < list.Count; i++)
            {
                lock (list[i])
                {
                    list[i].Dispose();
                }
            }
        }

        private void runServerCore()
        {
            //Create the connection for all the servers
            vrpnConnection = Connection.CreateServerConnection();

            //Set up all the analog servers
            analogServers = new List<AnalogServer>();
            for (int i = 0; i < serverMasterOptions.analogServers.Count; i++)
            {
                lock (serverMasterOptions.analogServers[i])
                {
                    //Note: This uses maxChannelUsed NOT trueChannelCount in case non-consecutive channels are used
                    analogServers.Add(new AnalogServer(serverMasterOptions.analogServers[i].serverName, vrpnConnection, serverMasterOptions.analogServers[i].maxChannelUsed + 1));
                    analogServers[i].MuteWarnings = !verbose;
                }
            }

            //Set up all the button servers
            buttonServers = new List<ButtonServer>();
            for (int i = 0; i < serverMasterOptions.buttonServers.Count; i++)
            {
                lock (serverMasterOptions.buttonServers[i])
                {
                    //Note: This uses maxButtonUsed NOT trueButtonCount in case non-consecutive buttons are used
                    buttonServers.Add(new ButtonServer(serverMasterOptions.buttonServers[i].serverName, vrpnConnection, serverMasterOptions.buttonServers[i].maxButtonUsed + 1));
                    buttonServers[i].MuteWarnings = !verbose;
                }
            }

            //Set up all the text servers
            textServers = new List<TextSender>();
            for (int i = 0; i < serverMasterOptions.textServers.Count; i++)
            {
                lock (serverMasterOptions.textServers[i])
                {
                    textServers.Add(new TextSender(serverMasterOptions.textServers[i].serverName, vrpnConnection));
                    textServers[i].MuteWarnings = !verbose;
                }
            }

            //Set up all the tracker servers
            trackerServers = new List<TrackerServer>();
            for (int i = 0; i < serverMasterOptions.trackerServers.Count; i++)
            {
                lock (serverMasterOptions.trackerServers[i])
                {
                    trackerServers.Add(new TrackerServer(serverMasterOptions.trackerServers[i].serverName, vrpnConnection, serverMasterOptions.trackerServers[i].sensorCount));
                    trackerServers[i].MuteWarnings = !verbose;
                }
            }

            //Subscribe to the Kinect events
            subscribeToKinectEvents();

            //Start the skeleton update timer
            //TODO: Is this update rate okay?
            skeletonUpdateTimer = new System.Timers.Timer(33); //Update at 30 FPS?
            skeletonUpdateTimer.Elapsed += skeletonUpdateTimer_Elapsed;
            skeletonUpdateTimer.Start();

            //The server isn't really running until everything is setup here.
            serverState = ServerRunState.Running;

            //Run the server
            while (!forceStop)
            {
                //Update the analog servers
                updateList(ref analogServers);
                updateList(ref buttonServers);
                updateList(ref textServers);
                updateList(ref trackerServers);
                lock (vrpnConnection)
                {
                    vrpnConnection.Update();
                }
                Thread.Yield(); // Be polite, but don't add unnecessary latency.
            }

            //Cleanup everything
            unsubscribeFromKinectEvents();
            skeletonUpdateTimer.Stop();
            skeletonUpdateTimer.Elapsed -= skeletonUpdateTimer_Elapsed;

            //Dispose the analog servers
            serverState = ServerRunState.Stopping;
            disposeList(ref analogServers);
            disposeList(ref buttonServers);
            disposeList(ref textServers);
            disposeList(ref trackerServers);
            lock (vrpnConnection)
            {
                vrpnConnection.Dispose();
            }

            serverState = ServerRunState.Stopped;
        }

        private void subscribeToKinectEvents()
        {
            for (int i = 0; i < serverMasterOptions.kinectOptionsList.Count; i++)
            {
                if (serverMasterOptions.kinectOptionsList[i].version == KinectVersion.KinectV1)
                {
                    KinectV1Wrapper.Settings tempSettings = (KinectV1Wrapper.Settings)serverMasterOptions.kinectOptionsList[i];
                    if (tempSettings.mergeSkeletons || tempSettings.sendRawSkeletons)
                    {
                        kinects[i].SkeletonChanged += kinect_SkeletonChanged;
                    }
                    if (tempSettings.sendAudioAngle)
                    {
                        kinects[i].AudioPositionChanged += kinect_AudioPositionChanged;
                    }
                    if (tempSettings.sendAcceleration)
                    {
                        kinects[i].AccelerationChanged += kinect_AccelerationChanged;
                    }
                }
                //TODO: Add handling for Kinect v2 events
                //TODO: Add handling for Networked Kinect events
            }
        }
        private void unsubscribeFromKinectEvents()
        {
            for (int i = 0; i < serverMasterOptions.kinectOptionsList.Count; i++)
            {
                if (serverMasterOptions.kinectOptionsList[i].version == KinectVersion.KinectV1)
                {
                    KinectV1Wrapper.Settings tempSettings = (KinectV1Wrapper.Settings)serverMasterOptions.kinectOptionsList[i];
                    if (tempSettings.mergeSkeletons || tempSettings.sendRawSkeletons)
                    {
                        kinects[i].SkeletonChanged -= kinect_SkeletonChanged;
                    }
                    if (tempSettings.sendAudioAngle)
                    {
                        kinects[i].AudioPositionChanged -= kinect_AudioPositionChanged;
                    }
                    if (tempSettings.sendAcceleration)
                    {
                        kinects[i].AccelerationChanged -= kinect_AccelerationChanged;
                    }
                }
                //TODO: Add handling for Kinect v2 events
                //TODO: Add handling for networked Kinect events
            }
        }

        private void kinect_AccelerationChanged(object sender, AccelerationEventArgs e)
        {
            //Transmit the acceleration
            if (isRunning)
            {
                if (serverMasterOptions.kinectOptionsList[e.kinectID].version == KinectVersion.KinectV1)
                {
                    KinectV1Wrapper.Settings tempSettings = ((KinectV1Wrapper.Settings)serverMasterOptions.kinectOptionsList[e.kinectID]);
                    if (tempSettings.sendAcceleration)
                    {
                        for (int i = 0; i < analogServers.Count; i++)
                        {
                            if (serverMasterOptions.analogServers[i].serverName == tempSettings.accelerationServerName)
                            {
                                if (e.acceleration.HasValue)
                                {
                                    lock (analogServers[i])
                                    {
                                        analogServers[i].AnalogChannels[tempSettings.accelXChannel].Value = e.acceleration.Value.X;
                                        analogServers[i].AnalogChannels[tempSettings.accelYChannel].Value = e.acceleration.Value.Y;
                                        analogServers[i].AnalogChannels[tempSettings.accelZChannel].Value = e.acceleration.Value.Z;
                                        analogServers[i].Report();
                                    }
                                }
                                break;
                            }
                        }
                    }
                }
                else if (serverMasterOptions.kinectOptionsList[e.kinectID].version == KinectVersion.KinectV2)
                {
                    //TODO: Setup transmitting for the Kinect v2
                    //KinectV1Wrapper.Settings tempSettings = ((KinectV1Wrapper.Settings)serverMasterOptions.kinectOptionsList[e.kinectID]);
                    //if (tempSettings.sendAcceleration)
                    //{
                    //    for (int i = 0; i < analogServers.Count; i++)
                    //    {
                    //        if (serverMasterOptions.analogServers[i].serverName == tempSettings.accelerationServerName)
                    //        {
                    //            lock (analogServers[i])
                    //            {
                    //                analogServers[i].AnalogChannels[tempSettings.accelXChannel].Value = e.acceleration.X;
                    //                analogServers[i].AnalogChannels[tempSettings.accelYChannel].Value = e.acceleration.Y;
                    //                analogServers[i].AnalogChannels[tempSettings.accelZChannel].Value = e.acceleration.Z;
                    //                analogServers[i].Report();
                    //            }
                    //            break;
                    //        }
                    //    }
                    //}
                }
            }
        }
        private void kinect_AudioPositionChanged(object sender, AudioPositionEventArgs e)
        {
            if (isRunning)
            {
                if (serverMasterOptions.kinectOptionsList[e.kinectID].version == KinectVersion.KinectV1)
                {
                    KinectV1Wrapper.Settings tempSettings = ((KinectV1Wrapper.Settings)serverMasterOptions.kinectOptionsList[e.kinectID]);
                    if (tempSettings.sendAudioAngle)
                    {
                        for (int i = 0; i < analogServers.Count; i++)
                        {
                            if (serverMasterOptions.analogServers[i].serverName == tempSettings.audioAngleServerName)
                            {
                                lock (analogServers[i])
                                {
                                    analogServers[i].AnalogChannels[tempSettings.audioAngleChannel].Value = e.audioAngle;
                                    analogServers[i].Report();
                                }
                                break;
                            }
                        }
                    }
                }
                else if (serverMasterOptions.kinectOptionsList[e.kinectID].version == KinectVersion.KinectV2)
                {
                    //TODO: Transmit the audio angle from the Kinect v2
                }
            }
        }
        private void kinect_SkeletonChanged(object sender, SkeletonEventArgs e)
        {
            DateTime time = DateTime.UtcNow;

            if (isRunning)
            {
                //Transmit the Kinect v1 Skeletons
                if (serverMasterOptions.kinectOptionsList[e.kinectID].version == KinectVersion.KinectV1)
                {
                    KinectV1Wrapper.Settings tempSettings = (KinectV1Wrapper.Settings)serverMasterOptions.kinectOptionsList[e.kinectID];

                    //Send the raw skeletons
                    if (tempSettings.sendRawSkeletons)
                    {
                        //The skeletons need to be copied, so the e.skeletons version isn't changed when the transformation is done
                        KinectSkeleton[] skeletons = new KinectSkeleton[e.skeletons.Length];
                        Array.Copy(e.skeletons, skeletons, e.skeletons.Length);

                        //Transform the raw skeletons, if the user requested it
                        if (tempSettings.transformRawSkeletons)
                        {
                            for (int i = 0; i < skeletons.Length; i++)
                            {
                                skeletons[i] = kinects[e.kinectID].TransformSkeleton(skeletons[i]);
                            }
                        }

                        //Sort the raw skeletons
                        List<KinectSkeleton> sortedSkeletons = SortSkeletons(new List<KinectSkeleton>(skeletons), tempSettings.rawSkeletonSettings.skeletonSortMode);

                        //Transmit the skeleton data
                        for (int i = 0; i < sortedSkeletons.Count; i++)
                        {
                            //Transmit the joints
                            SendSkeletonVRPN(sortedSkeletons[i].skeleton, tempSettings.rawSkeletonSettings.individualSkeletons[i].serverName);

                            //Transmit the right hand
                            if (tempSettings.rawSkeletonSettings.individualSkeletons[i].useRightHandGrip)
                            {
                                SendHandStateVRPN(e.skeletons[i].rightHandClosed, tempSettings.rawSkeletonSettings.individualSkeletons[i].rightGripServerName, tempSettings.rawSkeletonSettings.individualSkeletons[i].rightGripButtonNumber);
                            }

                            //Transmit the right hand
                            if (tempSettings.rawSkeletonSettings.individualSkeletons[i].useLeftHandGrip)
                            {
                                SendHandStateVRPN(e.skeletons[i].rightHandClosed, tempSettings.rawSkeletonSettings.individualSkeletons[i].rightGripServerName, tempSettings.rawSkeletonSettings.individualSkeletons[i].rightGripButtonNumber);
                            }
                        }
                    }

                    //Merge the skeletons into the ones collected by the other Kinects
                    if (tempSettings.mergeSkeletons)
                    {
                        //Copy the skeletons to a temporary variable
                        KinectSkeleton[] skeletons = new KinectSkeleton[e.skeletons.Length];
                        Array.Copy(e.skeletons, skeletons, e.skeletons.Length);

                        //Transform the skeletons
                        for (int i = 0; i < skeletons.Length; i++)
                        {
                            skeletons[i] = kinects[e.kinectID].TransformSkeleton(skeletons[i]);
                        }

                        //Add the skeletons to the merge list
                        for (int i = 0; i < perKinectSkeletons.Count; i++)
                        {
                            if (perKinectSkeletons[i].uniqueID == kinects[e.kinectID].uniqueKinectID)
                            {
                                perKinectSkeletons.RemoveAt(i);
                            }
                        }
                        KinectSkeletonsData kinectSkel = new KinectSkeletonsData(kinects[e.kinectID].uniqueKinectID, e.skeletons.Length);
                        kinectSkel.actualSkeletons = new List<KinectSkeleton>(skeletons);
                        kinectSkel.kinectID = e.kinectID;
                        kinectSkel.utcTime = time;
                        perKinectSkeletons.Add(kinectSkel);
                    }

                    //Update the audio beam angle, if requested
                    if (((KinectV1Wrapper.Settings)serverMasterOptions.kinectOptionsList[e.kinectID]).audioTrackMode == AudioTrackingMode.LocalSkeletonX)
                    {
                        ((KinectV1Wrapper.Core)kinects[e.kinectID]).UpdateAudioAngle(e.skeletons[((KinectV1Wrapper.Settings)serverMasterOptions.kinectOptionsList[e.kinectID]).audioBeamTrackSkeletonNumber].Position);
                    }
                }
                else if (serverMasterOptions.kinectOptionsList[e.kinectID].version == KinectVersion.KinectV2)
                {
                    //TODO: Send the raw skeletons for the Kinect v2
                }
            }
        }

        //This function goes through the skeletons from all the Kinects and figures out which ones are the same
        //When multiple skeletons from different Kinects are the same, they will need to be merged together
        private void findSameSkeletons(List<KinectSkeletonsData> kinectSkeletons)
        {            
            KinectSkeletonsData[] skeletonsCopy;
            lock (kinectSkeletons)
            {
                //TODO: Fix threading issues
                //Even with the lock, the kinectSkeletons variable sometimes gets updated while we are inside this function
                skeletonsCopy = new KinectSkeletonsData[kinectSkeletons.Count];
                kinectSkeletons.CopyTo(0, skeletonsCopy, 0, skeletonsCopy.Length);
            }

            List<Point3D> averageCenters = new List<Point3D>();
            mergedSkeletons.Clear();

            for (int i = 0; i < skeletonsCopy.Length; i++) //For each Kinect
            {
                for (int j = 0; j < skeletonsCopy[i].actualSkeletons.Count; j++) //For each skeleton from the Kinect
                {
                    bool matchFound = false;
                    for (int k = 0; k < averageCenters.Count; k++)
                    {
                        Vector3D distance = averageCenters[k] - skeletonsCopy[i].actualSkeletons[j].Position;
                        if (Math.Abs(distance.Length) < 0.3)
                        {
                            matchFound = true;
                            averageCenters[k] = HelperMethods.IncAverage(averageCenters[k], skeletonsCopy[i].actualSkeletons[j].Position, mergedSkeletons[k].Count);
                            mergedSkeletons[k].AddSkeletonToMerge(skeletonsCopy[i].actualSkeletons[j]);
                        }
                    }

                    if (!matchFound)
                    {
                        mergedSkeletons.Add(new MergedSkeleton());
                        mergedSkeletons[mergedSkeletons.Count - 1].AddSkeletonToMerge(skeletonsCopy[i].actualSkeletons[j]);
                        averageCenters.Add(skeletonsCopy[i].actualSkeletons[j].Position);
                    }
                }
            }
        }
        private void skeletonUpdateTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            findSameSkeletons(perKinectSkeletons);

            List<MergedSkeleton> sortedSkeletons = SortSkeletons(mergedSkeletons, serverMasterOptions.mergedSkeletonOptions.skeletonSortMode);

            int usedSkeletonsCount = Math.Min(mergedSkeletons.Count, serverMasterOptions.mergedSkeletonOptions.individualSkeletons.Count);
            for (int i = 0; i < usedSkeletonsCount; i++)
            {
                //Send the skeleton data
                if (serverMasterOptions.mergedSkeletonOptions.individualSkeletons[i].useSkeleton)
                {
                    SendSkeletonVRPN(sortedSkeletons[i].skeleton, serverMasterOptions.mergedSkeletonOptions.individualSkeletons[i].serverName);
                }
                //Send the right hand grab data
                if (serverMasterOptions.mergedSkeletonOptions.individualSkeletons[i].useRightHandGrip)
                {
                    SendHandStateVRPN(sortedSkeletons[i].rightHandClosed, serverMasterOptions.mergedSkeletonOptions.individualSkeletons[i].rightGripServerName, serverMasterOptions.mergedSkeletonOptions.individualSkeletons[i].rightGripButtonNumber);
                }
                //Send the left hand grab data
                if (serverMasterOptions.mergedSkeletonOptions.individualSkeletons[i].useLeftHandGrip)
                {
                    SendHandStateVRPN(sortedSkeletons[i].leftHandClosed, serverMasterOptions.mergedSkeletonOptions.individualSkeletons[i].leftGripServerName, serverMasterOptions.mergedSkeletonOptions.individualSkeletons[i].leftGripButtonNumber);
                }
            }

            //Update the audio beam angles
            for (int i = 0; i < kinects.Count; i++)
            {
                if (serverMasterOptions.kinectOptionsList[i].version == KinectVersion.KinectV1)
                {
                    if (((KinectV1Wrapper.Settings)serverMasterOptions.kinectOptionsList[i]).audioTrackMode == AudioTrackingMode.MergedSkeletonX)
                    {
                        ((KinectV1Wrapper.Core)kinects[i]).UpdateAudioAngle(sortedSkeletons[((KinectV1Wrapper.Settings)serverMasterOptions.kinectOptionsList[i]).audioBeamTrackSkeletonNumber].Position);
                    }
                }
                else if (serverMasterOptions.kinectOptionsList[i].version == KinectVersion.KinectV2)
                {

                }
            }
        }

        private int GetSkeletonSensorNumber(JointType joint)
        {
            int sensorNumber = -1;

            //Translates the SDK joints to the FAAST joint numbers
            switch (joint)
            {
                case JointType.Head:
                    {
                        sensorNumber = 0;
                        break;
                    }
                case JointType.ShoulderCenter:
                    {
                        sensorNumber = 1;
                        break;
                    }
                case JointType.Spine:
                    {
                        sensorNumber = 2;
                        break;
                    }
                case JointType.HipCenter:
                    {
                        sensorNumber = 3;
                        break;
                    }
                //There is no 4, in order to match with FAAST (left collar in OpenNI)
                case JointType.ShoulderLeft:
                    {
                        sensorNumber = 5;
                        break;
                    }
                case JointType.ElbowLeft:
                    {
                        sensorNumber = 6;
                        break;
                    }
                case JointType.WristLeft:
                    {
                        sensorNumber = 7;
                        break;
                    }
                case JointType.HandLeft:
                    {
                        sensorNumber = 8;
                        break;
                    }
                case JointType.HandTipLeft: //Kinect v2 only
                    {
                        sensorNumber = 9;
                        break;
                    }
                //There is no 10, in order to match with FAAST (right collar in OpenNI)
                case JointType.ShoulderRight:
                    {
                        sensorNumber = 11;
                        break;
                    }
                case JointType.ElbowRight:
                    {
                        sensorNumber = 12;
                        break;
                    }
                case JointType.WristRight:
                    {
                        sensorNumber = 13;
                        break;
                    }
                case JointType.HandRight:
                    {
                        sensorNumber = 14;
                        break;
                    }
                case JointType.HandTipRight: //Kinect v2 only
                    {
                        sensorNumber = 15;
                        break;
                    }
                case JointType.HipLeft:
                    {
                        sensorNumber = 16;
                        break;
                    }
                case JointType.KneeLeft:
                    {
                        sensorNumber = 17;
                        break;
                    }
                case JointType.AnkleLeft:
                    {
                        sensorNumber = 18;
                        break;
                    }
                case JointType.FootLeft:
                    {
                        sensorNumber = 19;
                        break;
                    }
                case JointType.HipRight:
                    {
                        sensorNumber = 20;
                        break;
                    }
                case JointType.KneeRight:
                    {
                        sensorNumber = 21;
                        break;
                    }
                case JointType.AnkleRight:
                    {
                        sensorNumber = 22;
                        break;
                    }
                case JointType.FootRight:
                    {
                        sensorNumber = 23;
                        break;
                    }
                case JointType.Neck: //Kinect v2 only
                    {
                        sensorNumber = 24;
                        break;
                    }
                case JointType.ThumbLeft: //Kinect v2 only
                    {
                        sensorNumber = 25;
                        break;
                    }
                case JointType.ThumbRight: //Kinect v2 only
                    {
                        sensorNumber = 26;
                        break;
                    }
            }

            return sensorNumber;
        }
        //If any of the server names are null, the data for that server will be ignored
        private void SendSkeletonVRPN(SkeletonData skeleton, string trackingServerName)
        {
            int? jointServerID = GetServerIDFromName(trackingServerName, ServerType.Tracker);

            if (jointServerID.HasValue)
            {
                //foreach (Joint joint in skeleton)
                for (int i = 0; i < skeleton.Count; i++)
                {
                    Joint joint = skeleton[i];

                    //TODO: I am including inferred joints as well, should I? 
                    if (joint.TrackingState != TrackingState.NotTracked)
                    {
                        lock (trackerServers[jointServerID.Value])
                        {
                            trackerServers[jointServerID.Value].ReportPose(GetSkeletonSensorNumber(joint.JointType), DateTime.Now, joint.Position, joint.Orientation);
                        }
                    }
                }
            }
            else
            {
                HelperMethods.WriteToLog(String.Format("Could not find the ID of the tracking server {0}.", trackingServerName), parent);
            }
        }
        private void SendHandStateVRPN(bool state, string handServerName, int buttonNumber)
        {
            int? handServerID = GetServerIDFromName(handServerName, ServerType.Button);

            if (handServerID.HasValue)
            {
                lock (buttonServers[handServerID.Value])
                {
                    //TODO: Fix crash here when the server closes (null reference on the button server)
                    buttonServers[handServerID.Value].Buttons[buttonNumber] = state;
                }
            }
            else
            {
                HelperMethods.WriteToLog(String.Format("Could not find the ID of the button server {0}.", handServerName), parent);
            }
        }
        private List<KinectSkeleton> SortSkeletons(List<KinectSkeleton> unsortedSkeletons, SkeletonSortMethod sortMethod)
        {
            if (sortMethod == SkeletonSortMethod.NoSort)
            {
                return unsortedSkeletons;
            }
            else
            {
                //TODO: What point do I want to sort by?  Right now i am using head, but should i use something else?
                //Seperate the tracked and untracked skeletons
                List<KinectSkeleton> trackedSkeletons = new List<KinectSkeleton>();
                List<KinectSkeleton> untrackedSkeletons = new List<KinectSkeleton>();
                for (int i = 0; i < unsortedSkeletons.Count; i++)
                {
                    if (unsortedSkeletons[i].SkeletonTrackingState == TrackingState.NotTracked)
                    {
                        untrackedSkeletons.Add(unsortedSkeletons[i]);
                    }
                    else
                    {
                        trackedSkeletons.Add(unsortedSkeletons[i]);
                    }
                }

                if (sortMethod == SkeletonSortMethod.OriginXClosest || sortMethod == SkeletonSortMethod.OriginXFarthest)
                {
                    //We only care about the tracked skeletons, so only sort those
                    for (int i = 1; i < trackedSkeletons.Count; i++)
                    {
                        int insertIndex = i;
                        KinectSkeleton tempSkeleton = trackedSkeletons[i];

                        while (insertIndex > 0 && Math.Abs(tempSkeleton.Position.X) < Math.Abs(trackedSkeletons[insertIndex - 1].Position.X))
                        {
                            trackedSkeletons[insertIndex] = trackedSkeletons[insertIndex - 1];
                            insertIndex--;
                        }
                        trackedSkeletons[insertIndex] = tempSkeleton;
                    }

                    if (sortMethod == SkeletonSortMethod.OriginXFarthest)
                    {
                        trackedSkeletons.Reverse();
                    }
                }
                else if (sortMethod == SkeletonSortMethod.OriginYClosest || sortMethod == SkeletonSortMethod.OriginYFarthest)
                {
                    //We only care about the tracked skeletons, so only sort those
                    for (int i = 1; i < trackedSkeletons.Count; i++)
                    {
                        int insertIndex = i;
                        KinectSkeleton tempSkeleton = trackedSkeletons[i];

                        while (insertIndex > 0 && Math.Abs(tempSkeleton.Position.Y) < Math.Abs(trackedSkeletons[insertIndex - 1].Position.Y))
                        {
                            trackedSkeletons[insertIndex] = trackedSkeletons[insertIndex - 1];
                            insertIndex--;
                        }
                        trackedSkeletons[insertIndex] = tempSkeleton;
                    }

                    if (sortMethod == SkeletonSortMethod.OriginYFarthest)
                    {
                        trackedSkeletons.Reverse();
                    }
                }
                else if (sortMethod == SkeletonSortMethod.OriginZClosest || sortMethod == SkeletonSortMethod.OriginZFarthest)
                {
                    //We only care about the tracked skeletons, so only sort those
                    for (int i = 1; i < trackedSkeletons.Count; i++)
                    {
                        int insertIndex = i;
                        KinectSkeleton tempSkeleton = trackedSkeletons[i];

                        while (insertIndex > 0 && Math.Abs(tempSkeleton.Position.Z) < Math.Abs(trackedSkeletons[insertIndex - 1].Position.Z))
                        {
                            trackedSkeletons[insertIndex] = trackedSkeletons[insertIndex - 1];
                            insertIndex--;
                        }
                        trackedSkeletons[insertIndex] = tempSkeleton;
                    }

                    if (sortMethod == SkeletonSortMethod.OriginZFarthest)
                    {
                        trackedSkeletons.Reverse();
                    }
                }
                else if (sortMethod == SkeletonSortMethod.OriginEuclidClosest || sortMethod == SkeletonSortMethod.OriginEuclidFarthest)
                {
                    //We only care about the tracked skeletons, so only sort those
                    for (int i = 1; i < trackedSkeletons.Count; i++)
                    {
                        int insertIndex = i;
                        KinectSkeleton tempSkeleton = trackedSkeletons[i];
                        Point3D origin = new Point3D(0, 0, 0);
                        double tempDistance = (origin - trackedSkeletons[i].Position).Length;

                        while (insertIndex > 0 && tempDistance < (origin - trackedSkeletons[insertIndex - 1].Position).Length)
                        {
                            trackedSkeletons[insertIndex] = trackedSkeletons[insertIndex - 1];
                            insertIndex--;
                        }
                        trackedSkeletons[insertIndex] = tempSkeleton;
                    }

                    if (sortMethod == SkeletonSortMethod.OriginEuclidFarthest)
                    {
                        trackedSkeletons.Reverse();
                    }
                }
                else if (feedbackPosition != null)  //Sort based on the feedback position, if it isn't null
                {
                    if (sortMethod == SkeletonSortMethod.FeedbackXClosest || sortMethod == SkeletonSortMethod.FeedbackXFarthest)
                    {
                        //We only care about the tracked skeletons, so only sort those
                        for (int i = 1; i < trackedSkeletons.Count; i++)
                        {
                            int insertIndex = i;
                            KinectSkeleton tempSkeleton = trackedSkeletons[i];

                            while (insertIndex > 0 && Math.Abs(tempSkeleton.Position.X - feedbackPosition.Value.X) < Math.Abs(trackedSkeletons[insertIndex - 1].Position.X - feedbackPosition.Value.X))
                            {
                                trackedSkeletons[insertIndex] = trackedSkeletons[insertIndex - 1];
                                insertIndex--;
                            }
                            trackedSkeletons[insertIndex] = tempSkeleton;
                        }

                        if (sortMethod == SkeletonSortMethod.FeedbackXFarthest)
                        {
                            trackedSkeletons.Reverse();
                        }
                    }
                    else if (sortMethod == SkeletonSortMethod.FeedbackYClosest || sortMethod == SkeletonSortMethod.FeedbackYFarthest)
                    {
                        //We only care about the tracked skeletons, so only sort those
                        for (int i = 1; i < trackedSkeletons.Count; i++)
                        {
                            int insertIndex = i;
                            KinectSkeleton tempSkeleton = trackedSkeletons[i];

                            while (insertIndex > 0 && Math.Abs(tempSkeleton.Position.Y - feedbackPosition.Value.Y) < Math.Abs(trackedSkeletons[insertIndex - 1].Position.Y - feedbackPosition.Value.Y))
                            {
                                trackedSkeletons[insertIndex] = trackedSkeletons[insertIndex - 1];
                                insertIndex--;
                            }
                            trackedSkeletons[insertIndex] = tempSkeleton;
                        }

                        if (sortMethod == SkeletonSortMethod.FeedbackYFarthest)
                        {
                            trackedSkeletons.Reverse();
                        }
                    }
                    else if (sortMethod == SkeletonSortMethod.FeedbackZClosest || sortMethod == SkeletonSortMethod.FeedbackZFarthest)
                    {
                        //We only care about the tracked skeletons, so only sort those
                        for (int i = 1; i < trackedSkeletons.Count; i++)
                        {
                            int insertIndex = i;
                            KinectSkeleton tempSkeleton = trackedSkeletons[i];

                            while (insertIndex > 0 && Math.Abs(tempSkeleton.Position.Z - feedbackPosition.Value.Z) < Math.Abs(trackedSkeletons[insertIndex - 1].Position.Z - feedbackPosition.Value.Z))
                            {
                                trackedSkeletons[insertIndex] = trackedSkeletons[insertIndex - 1];
                                insertIndex--;
                            }
                            trackedSkeletons[insertIndex] = tempSkeleton;
                        }

                        if (sortMethod == SkeletonSortMethod.FeedbackZFarthest)
                        {
                            trackedSkeletons.Reverse();
                        }
                    }
                    else if (sortMethod == SkeletonSortMethod.FeedbackEuclidClosest || sortMethod == SkeletonSortMethod.FeedbackEuclidFarthest)
                    {
                        //We only care about the tracked skeletons, so only sort those
                        for (int i = 1; i < trackedSkeletons.Count; i++)
                        {
                            int insertIndex = i;
                            KinectSkeleton tempSkeleton = trackedSkeletons[i];
                            //SkeletonPoint feedPosition = new SkeletonPoint() { X = (float)feedbackPosition.Value.X, Y = (float)feedbackPosition.Value.Y, Z = (float)feedbackPosition.Value.Z };
                            //double tempDistance = InterPointDistance(feedPosition, trackedSkeletons[i].skeleton.Position);
                            double tempDistance = (feedbackPosition.Value - trackedSkeletons[i].Position).Length;

                            while (insertIndex > 0 && tempDistance < (feedbackPosition.Value - trackedSkeletons[insertIndex - 1].Position).Length)
                            {
                                trackedSkeletons[insertIndex] = trackedSkeletons[insertIndex - 1];
                                insertIndex--;
                            }
                            trackedSkeletons[insertIndex] = tempSkeleton;
                        }

                        if (sortMethod == SkeletonSortMethod.FeedbackEuclidFarthest)
                        {
                            trackedSkeletons.Reverse();
                        }
                    }
                    else
                    {
                        return unsortedSkeletons;
                    }
                }
                else
                {
                    return unsortedSkeletons;
                }

                //Add the untracked skeletons to the tracked ones before sending everything back
                trackedSkeletons.AddRange(untrackedSkeletons);

                return trackedSkeletons;
            }
        }
        private List<MergedSkeleton> SortSkeletons(List<MergedSkeleton> unsortedSkeletons, SkeletonSortMethod sortMethod)
        {
            if (sortMethod == SkeletonSortMethod.NoSort)
            {
                return unsortedSkeletons;
            }
            else
            {
                //TODO: What point do I want to sort by?  Right now i am using head, but should i use something else?
                //Seperate the tracked and untracked skeletons
                List<MergedSkeleton> trackedSkeletons = new List<MergedSkeleton>();
                List<MergedSkeleton> untrackedSkeletons = new List<MergedSkeleton>();
                for (int i = 0; i < unsortedSkeletons.Count; i++)
                {
                    if (unsortedSkeletons[i].SkeletonTrackingState == TrackingState.NotTracked)
                    {
                        untrackedSkeletons.Add(unsortedSkeletons[i]);
                    }
                    else
                    {
                        trackedSkeletons.Add(unsortedSkeletons[i]);
                    }
                }

                if (sortMethod == SkeletonSortMethod.OriginXClosest || sortMethod == SkeletonSortMethod.OriginXFarthest)
                {
                    //We only care about the tracked skeletons, so only sort those
                    for (int i = 1; i < trackedSkeletons.Count; i++)
                    {
                        int insertIndex = i;
                        MergedSkeleton tempSkeleton = trackedSkeletons[i];

                        while (insertIndex > 0 && Math.Abs(tempSkeleton.Position.X) < Math.Abs(trackedSkeletons[insertIndex - 1].Position.X))
                        {
                            trackedSkeletons[insertIndex] = trackedSkeletons[insertIndex - 1];
                            insertIndex--;
                        }
                        trackedSkeletons[insertIndex] = tempSkeleton;
                    }

                    if (sortMethod == SkeletonSortMethod.OriginXFarthest)
                    {
                        trackedSkeletons.Reverse();
                    }
                }
                else if (sortMethod == SkeletonSortMethod.OriginYClosest || sortMethod == SkeletonSortMethod.OriginYFarthest)
                {
                    //We only care about the tracked skeletons, so only sort those
                    for (int i = 1; i < trackedSkeletons.Count; i++)
                    {
                        int insertIndex = i;
                        MergedSkeleton tempSkeleton = trackedSkeletons[i];

                        while (insertIndex > 0 && Math.Abs(tempSkeleton.Position.Y) < Math.Abs(trackedSkeletons[insertIndex - 1].Position.Y))
                        {
                            trackedSkeletons[insertIndex] = trackedSkeletons[insertIndex - 1];
                            insertIndex--;
                        }
                        trackedSkeletons[insertIndex] = tempSkeleton;
                    }

                    if (sortMethod == SkeletonSortMethod.OriginYFarthest)
                    {
                        trackedSkeletons.Reverse();
                    }
                }
                else if (sortMethod == SkeletonSortMethod.OriginZClosest || sortMethod == SkeletonSortMethod.OriginZFarthest)
                {
                    //We only care about the tracked skeletons, so only sort those
                    for (int i = 1; i < trackedSkeletons.Count; i++)
                    {
                        int insertIndex = i;
                        MergedSkeleton tempSkeleton = trackedSkeletons[i];

                        while (insertIndex > 0 && Math.Abs(tempSkeleton.Position.Z) < Math.Abs(trackedSkeletons[insertIndex - 1].Position.Z))
                        {
                            trackedSkeletons[insertIndex] = trackedSkeletons[insertIndex - 1];
                            insertIndex--;
                        }
                        trackedSkeletons[insertIndex] = tempSkeleton;
                    }

                    if (sortMethod == SkeletonSortMethod.OriginZFarthest)
                    {
                        trackedSkeletons.Reverse();
                    }
                }
                else if (sortMethod == SkeletonSortMethod.OriginEuclidClosest || sortMethod == SkeletonSortMethod.OriginEuclidFarthest)
                {
                    //We only care about the tracked skeletons, so only sort those
                    for (int i = 1; i < trackedSkeletons.Count; i++)
                    {
                        int insertIndex = i;
                        MergedSkeleton tempSkeleton = trackedSkeletons[i];
                        Point3D origin = new Point3D(0, 0, 0);
                        double tempDistance = (origin - trackedSkeletons[i].Position).Length;

                        while (insertIndex > 0 && tempDistance < (origin - trackedSkeletons[insertIndex - 1].Position).Length)
                        {
                            trackedSkeletons[insertIndex] = trackedSkeletons[insertIndex - 1];
                            insertIndex--;
                        }
                        trackedSkeletons[insertIndex] = tempSkeleton;
                    }

                    if (sortMethod == SkeletonSortMethod.OriginEuclidFarthest)
                    {
                        trackedSkeletons.Reverse();
                    }
                }
                else if (feedbackPosition != null)  //Sort based on the feedback position, if it isn't null
                {
                    if (sortMethod == SkeletonSortMethod.FeedbackXClosest || sortMethod == SkeletonSortMethod.FeedbackXFarthest)
                    {
                        //We only care about the tracked skeletons, so only sort those
                        for (int i = 1; i < trackedSkeletons.Count; i++)
                        {
                            int insertIndex = i;
                            MergedSkeleton tempSkeleton = trackedSkeletons[i];

                            while (insertIndex > 0 && Math.Abs(tempSkeleton.Position.X - feedbackPosition.Value.X) < Math.Abs(trackedSkeletons[insertIndex - 1].Position.X - feedbackPosition.Value.X))
                            {
                                trackedSkeletons[insertIndex] = trackedSkeletons[insertIndex - 1];
                                insertIndex--;
                            }
                            trackedSkeletons[insertIndex] = tempSkeleton;
                        }

                        if (sortMethod == SkeletonSortMethod.FeedbackXFarthest)
                        {
                            trackedSkeletons.Reverse();
                        }
                    }
                    else if (sortMethod == SkeletonSortMethod.FeedbackYClosest || sortMethod == SkeletonSortMethod.FeedbackYFarthest)
                    {
                        //We only care about the tracked skeletons, so only sort those
                        for (int i = 1; i < trackedSkeletons.Count; i++)
                        {
                            int insertIndex = i;
                            MergedSkeleton tempSkeleton = trackedSkeletons[i];

                            while (insertIndex > 0 && Math.Abs(tempSkeleton.Position.Y - feedbackPosition.Value.Y) < Math.Abs(trackedSkeletons[insertIndex - 1].Position.Y - feedbackPosition.Value.Y))
                            {
                                trackedSkeletons[insertIndex] = trackedSkeletons[insertIndex - 1];
                                insertIndex--;
                            }
                            trackedSkeletons[insertIndex] = tempSkeleton;
                        }

                        if (sortMethod == SkeletonSortMethod.FeedbackYFarthest)
                        {
                            trackedSkeletons.Reverse();
                        }
                    }
                    else if (sortMethod == SkeletonSortMethod.FeedbackZClosest || sortMethod == SkeletonSortMethod.FeedbackZFarthest)
                    {
                        //We only care about the tracked skeletons, so only sort those
                        for (int i = 1; i < trackedSkeletons.Count; i++)
                        {
                            int insertIndex = i;
                            MergedSkeleton tempSkeleton = trackedSkeletons[i];

                            while (insertIndex > 0 && Math.Abs(tempSkeleton.Position.Z - feedbackPosition.Value.Z) < Math.Abs(trackedSkeletons[insertIndex - 1].Position.Z - feedbackPosition.Value.Z))
                            {
                                trackedSkeletons[insertIndex] = trackedSkeletons[insertIndex - 1];
                                insertIndex--;
                            }
                            trackedSkeletons[insertIndex] = tempSkeleton;
                        }

                        if (sortMethod == SkeletonSortMethod.FeedbackZFarthest)
                        {
                            trackedSkeletons.Reverse();
                        }
                    }
                    else if (sortMethod == SkeletonSortMethod.FeedbackEuclidClosest || sortMethod == SkeletonSortMethod.FeedbackEuclidFarthest)
                    {
                        //We only care about the tracked skeletons, so only sort those
                        for (int i = 1; i < trackedSkeletons.Count; i++)
                        {
                            int insertIndex = i;
                            MergedSkeleton tempSkeleton = trackedSkeletons[i];
                            double tempDistance = (feedbackPosition.Value - trackedSkeletons[i].Position).Length;

                            while (insertIndex > 0 && tempDistance < (feedbackPosition.Value - trackedSkeletons[insertIndex - 1].Position).Length)
                            {
                                trackedSkeletons[insertIndex] = trackedSkeletons[insertIndex - 1];
                                insertIndex--;
                            }
                            trackedSkeletons[insertIndex] = tempSkeleton;
                        }

                        if (sortMethod == SkeletonSortMethod.FeedbackEuclidFarthest)
                        {
                            trackedSkeletons.Reverse();
                        }
                    }
                    else
                    {
                        return unsortedSkeletons;
                    }
                }
                else
                {
                    return unsortedSkeletons;
                }

                //Add the untracked skeletons to the tracked ones before sending everything back
                trackedSkeletons.AddRange(untrackedSkeletons);

                return trackedSkeletons;
            }
        }
        private int? GetServerIDFromName(string name, ServerType type)
        {
            if (type == ServerType.Analog)
            {
                for (int i = 0; i < serverMasterOptions.analogServers.Count; i++)
                {
                    if (serverMasterOptions.analogServers[i].serverName == name)
                    {
                        return i;
                    }
                }
            }
            else if (type == ServerType.Button)
            {
                for (int i = 0; i < serverMasterOptions.buttonServers.Count; i++)
                {
                    if (serverMasterOptions.buttonServers[i].serverName == name)
                    {
                        return i;
                    }
                }
            }
            else if (type == ServerType.Text)
            {
                for (int i = 0; i < serverMasterOptions.textServers.Count; i++)
                {
                    if (serverMasterOptions.textServers[i].serverName == name)
                    {
                        return i;
                    }
                }
            }
            else if (type == ServerType.Tracker)
            {
                for (int i = 0; i < serverMasterOptions.trackerServers.Count; i++)
                {
                    if (serverMasterOptions.trackerServers[i].serverName == name)
                    {
                        return i;
                    }
                }
            }
            return null; //Return null if the server isn't found
            //TODO: Add support for imager server
        }

        #region Settings parsing functions
        //This was moved from the settings to here because it needs to know about the per kinect options which are dependent on the different core dlls
        internal bool parseSettings(out string errorMessage)
        {
            bool settingsValid = true;
            errorMessage = "";

            serverMasterOptions.analogServers = new List<AnalogServerSettings>();
            serverMasterOptions.buttonServers = new List<ButtonServerSettings>();
            serverMasterOptions.textServers = new List<TextServerSettings>();
            serverMasterOptions.trackerServers = new List<TrackerServerSettings>();
            serverMasterOptions.imagerServers = new List<ImagerServerSettings>();

            #region Parse the Skeleton servers
            settingsValid &= parseIndividualSkeletons(serverMasterOptions.mergedSkeletonOptions.individualSkeletons, ref errorMessage);
            for (int i = 0; i < serverMasterOptions.kinectOptionsList.Count; i++)
            {
                if (serverMasterOptions.kinectOptionsList[i].version == KinectVersion.KinectV1)
                {
                    if (((KinectV1Wrapper.Settings)serverMasterOptions.kinectOptionsList[i]).sendRawSkeletons)
                    {
                        settingsValid &= parseIndividualSkeletons(((KinectV1Wrapper.Settings)serverMasterOptions.kinectOptionsList[i]).rawSkeletonSettings.individualSkeletons, ref errorMessage);
                    }
                }
                else if (serverMasterOptions.kinectOptionsList[i].version == KinectVersion.KinectV2)
                {
                    //TODO: Add parsing for Kinect v2 raw skeleton transmission
                }
            }
            #endregion

            #region Parse the Voice Commands
            for (int i = 0; i < serverMasterOptions.voiceCommands.Count; i++)
            {
                if (serverMasterOptions.voiceCommands[i].serverType == ServerType.Button)
                {
                    bool found = false;

                    if (isServerNameValid(serverMasterOptions.voiceCommands[i].serverName))
                    {
                        //Check if the button server already exists
                        for (int j = 0; j < serverMasterOptions.buttonServers.Count; j++)
                        {
                            if (serverMasterOptions.buttonServers[j].serverName == serverMasterOptions.voiceCommands[i].serverName)
                            {
                                //The button server exists, so lets see if it is using a unique button channel
                                found = true;
                                if (isServerButtonNumberValid(((VoiceButtonCommand)serverMasterOptions.voiceCommands[i]).buttonNumber))
                                {
                                    if (!serverMasterOptions.buttonServers[j].uniqueChannels.Contains(((VoiceButtonCommand)serverMasterOptions.voiceCommands[i]).buttonNumber))
                                    {
                                        serverMasterOptions.buttonServers[j].uniqueChannels.Add(((VoiceButtonCommand)serverMasterOptions.voiceCommands[i]).buttonNumber);
                                    }
                                }
                                else
                                {
                                    settingsValid = false;
                                    errorMessage += "Voice command \"" + serverMasterOptions.voiceCommands[i].recognizedWord + "\" server channel (\"" + ((VoiceButtonCommand)serverMasterOptions.voiceCommands[i]).buttonNumber + "\") is invalid.\r\n";
                                }
                            }
                        }

                        //The button server did not exist, time to create it!
                        if (!found)
                        {
                            ButtonServerSettings temp = new ButtonServerSettings();
                            temp.serverName = serverMasterOptions.voiceCommands[i].serverName;
                            temp.uniqueChannels = new List<int>();
                            if (isServerButtonNumberValid(((VoiceButtonCommand)serverMasterOptions.voiceCommands[i]).buttonNumber))
                            {
                                temp.uniqueChannels.Add(((VoiceButtonCommand)serverMasterOptions.voiceCommands[i]).buttonNumber);
                            }
                            else
                            {
                                settingsValid = false;
                                errorMessage += "Voice command \"" + serverMasterOptions.voiceCommands[i].recognizedWord + "\" server channel (\"" + ((VoiceButtonCommand)serverMasterOptions.voiceCommands[i]).buttonNumber + "\") is invalid.\r\n";
                            }
                            serverMasterOptions.buttonServers.Add(temp);
                        }
                    }
                    else
                    {
                        settingsValid = false;
                        errorMessage += "Voice command \"" + serverMasterOptions.voiceCommands[i].recognizedWord + "\" server name (\"" + serverMasterOptions.voiceCommands[i].serverName + "\") is invalid.\r\n";
                    }
                }
                else if (serverMasterOptions.voiceCommands[i].serverType == ServerType.Text)
                {
                    bool found = false;

                    if (isServerNameValid(serverMasterOptions.voiceCommands[i].serverName))
                    {
                        //Check if the button server already exists
                        for (int j = 0; j < serverMasterOptions.textServers.Count; j++)
                        {
                            if (serverMasterOptions.textServers[j].serverName == serverMasterOptions.voiceCommands[i].serverName)
                            {
                                //The text server exists!  We don't need to check channels on text servers
                                found = true;
                            }
                        }

                        if (!found)
                        {
                            TextServerSettings temp = new TextServerSettings();
                            temp.serverName = serverMasterOptions.voiceCommands[i].serverName;
                            serverMasterOptions.textServers.Add(temp);
                        }
                    }
                    else
                    {
                        settingsValid = false;
                        errorMessage += "Voice command \"" + serverMasterOptions.voiceCommands[i].recognizedWord + "\" server name (\"" + serverMasterOptions.voiceCommands[i].serverName + "\") is invalid.\r\n";
                    }
                }
            }
            #endregion

            //TODO: Implement gesture parsing and error handling
            #region Parse the Gesture Commands
            //for (int i = 0; i < serverMasterOptions.gestureCommands.Count; i++)
            //{
            //    bool found = false;

            //    for (int j = 0; j < serverMasterOptions.buttonServers.Count; j++)
            //    {
            //        if (serverMasterOptions.buttonServers[j].serverName == serverMasterOptions.gestureCommands[i].serverName)
            //        {
            //            //The button server exists, so lets see if it is using a unique button channel
            //            found = true;
            //            if (!serverMasterOptions.buttonServers[j].uniqueChannels.Contains(((GestureCommand)serverMasterOptions.gestureCommands[i]).buttonNumber))
            //            {
            //                serverMasterOptions.buttonServers[j].uniqueChannels.Add(((GestureCommand)serverMasterOptions.gestureCommands[i]).buttonNumber);
            //            }
            //        }
            //    }

            //    if (!found)
            //    {
            //        ButtonServerSettings temp = new ButtonServerSettings();
            //        temp.serverName = serverMasterOptions.gestureCommands[i].serverName;
            //        temp.uniqueChannels = new List<int>();
            //        temp.uniqueChannels.Add(((GestureCommand)serverMasterOptions.gestureCommands[i]).buttonNumber);
            //        serverMasterOptions.buttonServers.Add(temp);
            //    }
            //}
            #endregion

            //TODO: Reimplement this in a way that works with the interface
            #region Parse the per Kinect (acceleration and audio angle) settings
            for (int i = 0; i < serverMasterOptions.kinectOptionsList.Count; i++)
            {
                if (serverMasterOptions.kinectOptionsList[i].version == KinectVersion.KinectV1)
                {
                    settingsValid &= parseKinectV1Settings(((KinectV1Wrapper.Settings)serverMasterOptions.kinectOptionsList[i]), ref errorMessage);
                }
                else if (serverMasterOptions.kinectOptionsList[i].version == KinectVersion.KinectV2)
                {
                    //TODO: Pasrse the per kinect options for the Kinect v2
                }
                else if (serverMasterOptions.kinectOptionsList[i].version == KinectVersion.NetworkKinect)
                {
                    //TODO: Parse the per kinect options for the networked kinects
                }
            }
            #endregion

            return settingsValid;
        }
        private bool isServerNameValid(string serverName)
        {
            bool valid = true;

            valid = serverName != null && serverName != "";
            //TODO: This checks if the servername is alphanumeric, are there any other valid characters?
            valid = valid && System.Text.RegularExpressions.Regex.IsMatch(serverName, @"^[a-zA-Z0-9]+$");

            return valid;
        }
        private bool isServerAnalogChannelValid(int channel)
        {
            bool valid = true;
            valid = channel >= 0 && channel < AnalogServer.MaxChannels;
            return valid;
        }
        private bool isServerButtonNumberValid(int buttonNumber)
        {
            bool valid = true;
            valid = buttonNumber >= 0 && buttonNumber < ButtonServer.MaxButtons;
            return valid;
        }
        private bool parseIndividualSkeletons(ObservableCollection<KinectBase.PerSkeletonSettings> individualSkeletons, ref string errorMessage)
        {
            bool settingsValid = true;
            if (errorMessage == null)
            {
                errorMessage = "";
            }
            
            for (int i = 0; i < individualSkeletons.Count; i++)
            {
                if (individualSkeletons[i].useSkeleton)
                {
                    //Add the skeleton servers
                    if (isServerNameValid(individualSkeletons[i].serverName))
                    {
                        bool serverFound = false;
                        for (int j = 0; j < serverMasterOptions.trackerServers.Count; j++) //This allows for multiple skeletons to be assigned to the same tracker, although I don't know why you would want to do that...
                        {
                            if (serverMasterOptions.trackerServers[j].serverName == individualSkeletons[i].serverName)
                            {
                                serverFound = true;
                            }
                        }

                        if (!serverFound)
                        {
                            serverMasterOptions.trackerServers.Add(new TrackerServerSettings() { sensorCount = 24, serverName = individualSkeletons[i].serverName });
                        }
                    }
                    else
                    {
                        settingsValid = false;
                        errorMessage += "Skeleton " + i.ToString() + " server name (\"" + individualSkeletons[i].serverName + "\") is invalid.\r\n";
                    }

                    //Parse the right hand grip
                    if (individualSkeletons[i].useRightHandGrip)
                    {
                        if (isServerNameValid(individualSkeletons[i].rightGripServerName))
                        {
                            bool found = false;
                            for (int j = 0; j < serverMasterOptions.buttonServers.Count; j++)
                            {
                                if (serverMasterOptions.buttonServers[j].serverName == individualSkeletons[i].rightGripServerName)
                                {
                                    found = true;
                                    if (isServerButtonNumberValid(individualSkeletons[i].rightGripButtonNumber))
                                    {
                                        if (!serverMasterOptions.buttonServers[j].uniqueChannels.Contains(individualSkeletons[i].rightGripButtonNumber))
                                        {
                                            serverMasterOptions.buttonServers[j].uniqueChannels.Add(individualSkeletons[i].rightGripButtonNumber);
                                        }
                                    }
                                    else
                                    {
                                        settingsValid = false;
                                        errorMessage += "Skeleton " + i.ToString() + " right-hand grip server channel (" + individualSkeletons[i].rightGripButtonNumber.ToString() + ") is invalid.\r\n";
                                    }
                                }
                            }

                            //If the button server doesn't exist, create it!
                            if (!found)
                            {
                                ButtonServerSettings temp = new ButtonServerSettings();
                                temp.serverName = individualSkeletons[i].rightGripServerName;
                                temp.uniqueChannels = new List<int>();

                                if (isServerButtonNumberValid(individualSkeletons[i].rightGripButtonNumber))
                                {
                                    temp.uniqueChannels.Add(individualSkeletons[i].rightGripButtonNumber);
                                }
                                else
                                {
                                    settingsValid = false;
                                    errorMessage += "Skeleton " + i.ToString() + " right-hand grip server channel (" + individualSkeletons[i].rightGripButtonNumber.ToString() + ") is invalid.\r\n";
                                }

                                serverMasterOptions.buttonServers.Add(temp);
                            }
                        }
                        else
                        {
                            settingsValid = false;
                            errorMessage += "Skeleton " + i.ToString() + " right-hand grip server name (\"" + individualSkeletons[i].rightGripServerName + "\") is invalid.\r\n";
                        }
                    }

                    //Parse the left hand grip
                    if (individualSkeletons[i].useLeftHandGrip)
                    {
                        if (isServerNameValid(individualSkeletons[i].leftGripServerName))
                        {
                            bool found = false;
                            for (int j = 0; j < serverMasterOptions.buttonServers.Count; j++)
                            {
                                if (serverMasterOptions.buttonServers[j].serverName == individualSkeletons[i].leftGripServerName)
                                {
                                    found = true;
                                    if (isServerButtonNumberValid(individualSkeletons[i].leftGripButtonNumber))
                                    {
                                        if (!serverMasterOptions.buttonServers[j].uniqueChannels.Contains(individualSkeletons[i].leftGripButtonNumber))
                                        {
                                            serverMasterOptions.buttonServers[j].uniqueChannels.Add(individualSkeletons[i].leftGripButtonNumber);
                                        }
                                    }
                                    else
                                    {
                                        settingsValid = false;
                                        errorMessage += "Skeleton " + i.ToString() + " left-hand grip server channel (" + individualSkeletons[i].leftGripButtonNumber.ToString() + ") is invalid.\r\n";
                                    }
                                }
                            }

                            //If the button server doesn't exist, create it!
                            if (!found)
                            {
                                ButtonServerSettings temp = new ButtonServerSettings();
                                temp.serverName = individualSkeletons[i].leftGripServerName;
                                temp.uniqueChannels = new List<int>();

                                if (isServerButtonNumberValid(individualSkeletons[i].leftGripButtonNumber))
                                {
                                    temp.uniqueChannels.Add(individualSkeletons[i].leftGripButtonNumber);
                                }
                                else
                                {
                                    settingsValid = false;
                                    errorMessage += "Skeleton " + i.ToString() + " left-hand grip server channel (" + individualSkeletons[i].leftGripButtonNumber.ToString() + ") is invalid.\r\n";
                                }

                                serverMasterOptions.buttonServers.Add(temp);
                            }
                        }
                        else
                        {
                            settingsValid = false;
                            errorMessage += "Skeleton " + i.ToString() + " left-hand grip server name (\"" + individualSkeletons[i].leftGripServerName + "\") is invalid.\r\n";
                        }
                    }
                }
            }

            return settingsValid;
        }
        private bool parseKinectV1Settings(KinectV1Wrapper.Settings settings, ref string errorMessage)
        {
            bool settingsValid = true;
            if (errorMessage == null)
            {
                errorMessage = "";
            }

            //Parse the acceleration options
            if (settings.sendAcceleration)
            {
                if (isServerNameValid(settings.accelerationServerName))
                {
                    bool found = false;

                    for (int j = 0; j < serverMasterOptions.analogServers.Count; j++)
                    {
                        if (serverMasterOptions.analogServers[j].serverName == settings.accelerationServerName)
                        {
                            found = true;

                            //Check the X acceleration channel
                            if (isServerAnalogChannelValid(settings.accelXChannel))
                            {
                                //If the channel doesn't exist, create it
                                if (!serverMasterOptions.analogServers[j].uniqueChannels.Contains(settings.accelXChannel))
                                {
                                    serverMasterOptions.analogServers[j].uniqueChannels.Add(settings.accelXChannel);
                                }
                            }
                            else
                            {
                                settingsValid = false;
                                errorMessage += "Kinect " + settings.kinectID.ToString() + " X acceleration channel (" + settings.accelXChannel.ToString() + ") is invalid.\r\n";
                            }

                            //Check the Y acceleration channel
                            if (isServerAnalogChannelValid(settings.accelYChannel))
                            {
                                //If the channel doesn't exist, create it
                                if (!serverMasterOptions.analogServers[j].uniqueChannels.Contains(settings.accelYChannel))
                                {
                                    serverMasterOptions.analogServers[j].uniqueChannels.Add(settings.accelYChannel);
                                }
                            }
                            else
                            {
                                settingsValid = false;
                                errorMessage += "Kinect " + settings.kinectID.ToString() + " Y acceleration channel (" + settings.accelYChannel.ToString() + ") is invalid.\r\n";
                            }

                            //Check the Z acceleration channel
                            if (isServerAnalogChannelValid(settings.accelZChannel))
                            {
                                //If the channel doesn't exist, create it
                                if (!serverMasterOptions.analogServers[j].uniqueChannels.Contains(settings.accelZChannel))
                                {
                                    serverMasterOptions.analogServers[j].uniqueChannels.Add(settings.accelZChannel);
                                }
                            }
                            else
                            {
                                settingsValid = false;
                                errorMessage += "Kinect " + settings.kinectID.ToString() + " Z acceleration channel (" + settings.accelZChannel.ToString() + ") is invalid.\r\n";
                            }

                            break;
                        }
                    }

                    if (!found)
                    {
                        AnalogServerSettings temp = new AnalogServerSettings();
                        temp.serverName = settings.accelerationServerName;
                        temp.uniqueChannels = new List<int>();

                        //Add the X channel, if it is valid (it is the first one added, so it must be unique)
                        if (isServerAnalogChannelValid(settings.accelXChannel))
                        {
                            temp.uniqueChannels.Add(settings.accelXChannel);
                        }
                        else
                        {
                            settingsValid = false;
                            errorMessage += "Kinect " + settings.kinectID.ToString() + " X acceleration channel (" + settings.accelXChannel.ToString() + ") is invalid.\r\n";
                        }

                        //Add the Y channel, if it is valid and unique
                        if (isServerAnalogChannelValid(settings.accelYChannel))
                        {
                            if (!temp.uniqueChannels.Contains(settings.accelYChannel))
                            {
                                temp.uniqueChannels.Add(settings.accelYChannel);
                            }
                        }
                        else
                        {
                            settingsValid = false;
                            errorMessage += "Kinect " + settings.kinectID.ToString() + " Y acceleration channel (" + settings.accelYChannel.ToString() + ") is invalid.\r\n";
                        }

                        //Add the Z channel, if it is valid and unique
                        if (isServerAnalogChannelValid(settings.accelZChannel))
                        {
                            if (!temp.uniqueChannels.Contains(settings.accelZChannel))
                            {
                                temp.uniqueChannels.Add(settings.accelZChannel);
                            }
                        }
                        else
                        {
                            settingsValid = false;
                            errorMessage += "Kinect " + settings.kinectID.ToString() + " Z acceleration channel (" + settings.accelZChannel.ToString() + ") is invalid.\r\n";
                        }

                        serverMasterOptions.analogServers.Add(temp);
                    }
                }
                else
                {
                    settingsValid = false;
                    errorMessage += "Kinect " + settings.kinectID.ToString() + " acceleration server name (\"" + settings.accelerationServerName + "\") is invalid.\r\n";
                }
            }

            //Parse audio source angle options
            if (settings.sendAudioAngle)
            {
                if (isServerNameValid(settings.audioAngleServerName))
                {
                    bool found = false;

                    for (int j = 0; j < serverMasterOptions.analogServers.Count; j++)
                    {
                        if (serverMasterOptions.analogServers[j].serverName == settings.audioAngleServerName)
                        {
                            found = true;

                            if (isServerAnalogChannelValid(settings.audioAngleChannel))
                            {
                                if (!serverMasterOptions.analogServers[j].uniqueChannels.Contains(settings.audioAngleChannel))
                                {
                                    serverMasterOptions.analogServers[j].uniqueChannels.Add(settings.audioAngleChannel);
                                }
                            }
                            else
                            {
                                settingsValid = false;
                                errorMessage += "Kinect " + settings.kinectID.ToString() + " audio angle server channel (" + settings.audioAngleChannel.ToString() + ") is invalid.\r\n";
                            }
                            break;
                        }
                    }

                    if (!found)
                    {
                        AnalogServerSettings temp = new AnalogServerSettings();
                        temp.serverName = settings.audioAngleServerName;
                        temp.uniqueChannels = new List<int>();
                        if (isServerAnalogChannelValid(settings.audioAngleChannel))
                        {
                            temp.uniqueChannels.Add(settings.audioAngleChannel);
                        }
                        else
                        {
                            settingsValid = false;
                            errorMessage += "Kinect " + settings.kinectID.ToString() + " audio angle server channel (" + settings.audioAngleChannel.ToString() + ") is invalid.\r\n";
                        }
                        serverMasterOptions.analogServers.Add(temp);
                    }
                }
                else
                {
                    settingsValid = false;
                    errorMessage += "Kinect " + settings.kinectID.ToString() + " audio angle server name (\"" + settings.audioAngleServerName + "\") is invalid.\r\n";
                }
            }

            return settingsValid;
        }
        #endregion

        private delegate void runServerCoreDelegate();
        private delegate void launchVoiceRecognizerDelegate();
    }

    enum ServerRunState {Starting, Running, Stopping, Stopped}
}