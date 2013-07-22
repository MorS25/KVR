﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Collections.ObjectModel;
using Microsoft.Kinect;

namespace KinectWithVRServer
{
    public class MasterSettings
    {
        public KinectSettings kinectOptions;
        public SoundSourceSettings soundOptions;
        public SkeletonSettings skeletonOptions;
        public List<AnalogServerSettings> analogServers;
        public List<ButtonServerSettings> buttonServers;
        public List<TextServerSettings> textServers;
        public List<TrackerServerSettings> trackerServers;

        public ObservableCollection<VoiceButtonCommand> voiceButtonCommands;
        public ObservableCollection<VoiceTextCommand> voiceTextCommands;
        public ObservableCollection<GestureCommand> gestureCommands;
        internal List<VoiceCommand> voiceCommands //This needs to be internal so the save method won't try to save it to the settings file
        {
            get
            {
                List<VoiceCommand> temp = new List<VoiceCommand>();
                temp.AddRange(voiceButtonCommands);
                temp.AddRange(voiceTextCommands);
                return temp;
            }
        }

        public MasterSettings()
        {
            kinectOptions = new KinectSettings();
            soundOptions = new SoundSourceSettings();
            skeletonOptions = new SkeletonSettings();
            voiceTextCommands = new ObservableCollection<VoiceTextCommand>();
            voiceButtonCommands = new ObservableCollection<VoiceButtonCommand>();
            gestureCommands = new ObservableCollection<GestureCommand>();
            analogServers = new List<AnalogServerSettings>();
            buttonServers = new List<ButtonServerSettings>();
            textServers = new List<TextServerSettings>();
            trackerServers = new List<TrackerServerSettings>();
        }

        public void parseSettings()
        {
            analogServers = new List<AnalogServerSettings>();
            buttonServers = new List<ButtonServerSettings>();
            textServers = new List<TextServerSettings>();
            trackerServers = new List<TrackerServerSettings>();
            
            bool sendAngle = false;
            for (int i = 0; i < voiceCommands.Count; i++)
            {
                if (voiceCommands[i].serverType == ServerType.Button)
                {
                    bool found = false;

                    //Set if we need to turn on the sound angle server
                    if (voiceCommands[i].sendSourceAngle)
                    {
                        sendAngle = true;
                    }

                    //Check if the button server already exists
                    for (int j = 0; j < buttonServers.Count; j++)
                    {
                        if (buttonServers[j].serverName == voiceCommands[i].serverName)
                        {
                            //The button server exists, so lets see if it is using a unique button channel
                            found = true;
                            if (!buttonServers[j].uniqueChannels.Contains(((VoiceButtonCommand)voiceCommands[i]).buttonNumber))
                            {
                                buttonServers[j].uniqueChannels.Add(((VoiceButtonCommand)voiceCommands[i]).buttonNumber);
                            }
                        }
                    }

                    //The button server did not exist, time to create it!
                    if (!found)
                    {
                        ButtonServerSettings temp = new ButtonServerSettings();
                        temp.serverName = voiceCommands[i].serverName;
                        temp.uniqueChannels = new List<int>();
                        temp.uniqueChannels.Add(((VoiceButtonCommand)voiceCommands[i]).buttonNumber);
                        buttonServers.Add(temp);
                    }
                }
                else if (voiceCommands[i].serverType == ServerType.Text)
                {
                    bool found = false;

                    //Se if we need to turn on the sound angle server
                    if (voiceCommands[i].sendSourceAngle)
                    {
                        sendAngle = true;
                    }

                    //Check if the button server already exists
                    for (int j = 0; j < textServers.Count; j++)
                    {
                        if (textServers[j].serverName == voiceCommands[i].serverName)
                        {
                            //The text server exists!  We don't need to check channels on text servers
                            found = true;
                        }
                    }

                    if (!found)
                    {
                        TextServerSettings temp = new TextServerSettings();
                        temp.serverName = voiceCommands[i].serverName;
                        textServers.Add(temp);
                    }
                }
            }

            //Setup the analog server for the sound angle
            if (sendAngle)
            {
                AnalogServerSettings angleServer = new AnalogServerSettings();
                angleServer.serverName = "KinectSoundAngle";
                angleServer.uniqueChannels = new List<int>(1);
                angleServer.uniqueChannels.Add(0);
                angleServer.channelCount = 1;
                analogServers.Add(angleServer);
            }

            //Gesture Parsing
            for (int i = 0; i < gestureCommands.Count; i++)
            {
                bool found = false;

                for (int j = 0; j < buttonServers.Count; j++)
                {
                    if (buttonServers[j].serverName == gestureCommands[i].serverName)
                    {
                        //The button server exists, so lets see if it is using a unique button channel
                        found = true;
                        if (!buttonServers[j].uniqueChannels.Contains(((GestureCommand)gestureCommands[i]).buttonNumber))
                        {
                            buttonServers[j].uniqueChannels.Add(((GestureCommand)gestureCommands[i]).buttonNumber);
                        }
                    }
                }

                if (!found)
                {
                    ButtonServerSettings temp = new ButtonServerSettings();
                    temp.serverName = gestureCommands[i].serverName;
                    temp.uniqueChannels = new List<int>();
                    temp.uniqueChannels.Add(((GestureCommand)gestureCommands[i]).buttonNumber);
                    buttonServers.Add(temp);
                }
            }

            //Count unique channels for each button server
            for (int i = 0; i < buttonServers.Count; i++)
            {
                buttonServers[i].buttonCount = buttonServers[i].uniqueChannels.Count;
            }

            //Setup the tracker servers for the skeletal tracking
            if (kinectOptions.trackSkeletons)
            {
                trackerServers.Add(new TrackerServerSettings() { sensorCount = 24, serverName = "Tracker00" });
                trackerServers.Add(new TrackerServerSettings() { sensorCount = 24, serverName = "Tracker01" });
                trackerServers.Add(new TrackerServerSettings() { sensorCount = 24, serverName = "Tracker02" });
                trackerServers.Add(new TrackerServerSettings() { sensorCount = 24, serverName = "Tracker03" });
                trackerServers.Add(new TrackerServerSettings() { sensorCount = 24, serverName = "Tracker04" });
                trackerServers.Add(new TrackerServerSettings() { sensorCount = 24, serverName = "Tracker05" });
            }
        }
    }

    public class KinectSettings
    {
        public bool trackSkeletons = true;
        public ColorImageFormat colorImageMode = ColorImageFormat.RgbResolution640x480Fps30;
        public DepthImageFormat depthImageMode = DepthImageFormat.Resolution640x480Fps30;
        public bool isNearMode = false;
        //public bool isSeatedMode = false;
        //public bool previewEnabled = true;
        public double sensorAngle = 0.0;
        public EchoCancellationMode echoMode = EchoCancellationMode.None;
        public bool autoGainEnabled = false;
        public bool useKinectAudio = true; //Else use the default input
        //Noise suppression?
        //Force IR off?
        //Skeleton Picker Mode?
        //Send hand states?

    }

    public class SoundSourceSettings
    {

    }

    public class SkeletonSettings
    {
        public bool EnableTrackingInNearRange { get; set; }
        public bool isSeatedMode { get; set; }
        public SkeletonSortMethod skeletonSortMode { get; set; }
    }

    public class AnalogServerSettings
    {
        public string serverName { get; set;}
        public int channelCount { get; set; }
        public List<int> uniqueChannels { get; set; }
    }

    public class ButtonServerSettings
    {
        public string serverName { get; set; }
        public int buttonCount { get; set; }
        public List<int> uniqueChannels { get; set; }
    }

    public class TextServerSettings
    {
        public string serverName { get; set; }
    }

    public class TrackerServerSettings
    {
        public string serverName { get; set; }
        public int sensorCount { get; set; }
    }

    public class Command
    {
        public string serverName { get; set; }
        public CommandType commandType { get; set; }
        public string comments { get; set; }  //I think this should either be changed to button name (for use in VR juggler JCONFs) or a seperate name should be added for it
    }

    public class VoiceCommand : Command
    {
        public ServerType serverType { get; set; }
        public double confidence { get; set; }
        public bool sendSourceAngle { get; set; }
        public string recognizedWord { get; set; }
        
        //public AnalogCommand sourceAngleCommand;
    }

    public class VoiceTextCommand : VoiceCommand
    {
        public VoiceTextCommand()
        {
            base.serverType = ServerType.Text;
            base.commandType = CommandType.Voice;
        }

        public string actionText { get; set; }
    }

    public class VoiceButtonCommand : VoiceCommand
    {
        public VoiceButtonCommand()
        {
            base.serverType = ServerType.Button;
            base.commandType = CommandType.Voice;
        }

        public ButtonType buttonType { get; set; }
        public int buttonNumber { get; set; }
        public bool initialState { get; set; }
        public bool setState { get; set; }
    }

    public class GestureCommand : Command
    {
        public GestureCommand()
        {
            base.commandType = CommandType.Gesture;
        }

        public ServerType serverType
        {
            get { return ServerType.Button; }
        }
        public GestureType gestureType { get; set; }
        public int buttonNumber { get; set; }
        public int skeletonNumber { get; set; }
        //This will likely need to be added to to handle recorded gestures
    }

    //public class AnalogCommand : Command
    //{
    //    int channel;
    //}


    //(*)Need to hide CommandType and ServerType from visible columns
    public enum CommandType { Voice, Gesture/*, Analog */}
    public enum ServerType { Button, Analog, Tracker, Text }
    public enum ButtonType { Setter, Toggle, Momentary }
    public enum SkeletonSortMethod {NoSort, Closest, Farthest}
    public enum GestureType { Grip, Recorded }
    public enum PressState { Pressed, Released }
}