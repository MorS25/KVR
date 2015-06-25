﻿using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace KinectBase
{
    public class HelperMethods
    {
        public static Key[] NumberKeys = {Key.NumPad0, Key.NumPad1, Key.NumPad2, Key.NumPad3, Key.NumPad4, Key.NumPad5, Key.NumPad6, Key.NumPad7, Key.NumPad8, Key.NumPad9,
                                    Key.D0, Key.D1, Key.D2, Key.D3, Key.D4, Key.D5, Key.D6, Key.D7, Key.D8, Key.D9, 
                                    Key.Return, Key.Enter, Key.Delete, Key.Back, Key.Left, Key.Right, Key.Tab, Key.OemMinus, Key.Subtract};
        
    }

    public struct Joint
    {
        public JointType JointType;
        public System.Windows.Media.Media3D.Point3D Position;
        public System.Windows.Media.Media3D.Quaternion Orientation;
        public TrackingState TrackingState;
        public TrackingConfidence Confidence;
        //TODO: Should the positional error for each Kinect be here?
        //TODO: What about temporal error?
    }

    public class KinectSkeletonsData// : INotifyPropertyChanged
    {
        public KinectSkeletonsData(string UniqueID, int skeletonCount)
        {
            utcTime = DateTime.UtcNow;
            uID = UniqueID;
            actualSkeletons = new List<KinectSkeleton>(skeletonCount);
            for (int i = 0; i < skeletonCount; i++)
            {
                actualSkeletons.Add(new KinectSkeleton());
            }
        }
        
        private string uID = "";

        public bool processed { get; set; }
        public int kinectID { get; set; }
        public string uniqueID
        {
            get { return uID; }
        }
        public DateTime utcTime {get; set;}

        public List<KinectSkeleton> actualSkeletons { get; set; }
    }

    public class KinectSkeletonsDataComparer : IComparer<KinectSkeletonsData>
    {
        //This just redirects the compare to a comparison on the KinectID property
        public int Compare(KinectSkeletonsData x, KinectSkeletonsData y)
        {
            return x.kinectID.CompareTo(y.kinectID);
        }
    }

    public class KinectSkeleton
    {
        public KinectSkeleton()
        {
            //skeleton = new List<Joint>();
            skeleton = new SkeletonData();
        }

        //public List<Joint> skeleton { get; set; }
        public SkeletonData skeleton;
        public System.Windows.Media.Media3D.Point3D Position;
        public TrackingState SkeletonTrackingState;
        public bool rightHandClosed;
        public bool leftHandClosed;
        public int sourceKinectID;
        public DateTime utcSampleTime;
        public int TrackingId;
        //Include time of last update?
    }

    public class SkeletonData
    {
        private Joint[] jointBacker;
        private const int totalJoints = 28; //This should always be the same as the number of items in the JointType enum
        
        public SkeletonData()
        {
            jointBacker = new Joint[totalJoints];

            for (int i = 0; i < totalJoints; i++)
            {
                Joint temp = new Joint();
                temp.Confidence = TrackingConfidence.Unknown;
                temp.JointType = (JointType)i;
                temp.Orientation = System.Windows.Media.Media3D.Quaternion.Identity;
                temp.Position = new System.Windows.Media.Media3D.Point3D(0, 0, 0);
                temp.TrackingState = TrackingState.NotTracked;
                jointBacker[i] = temp;
            }
        }

        public int Count
        {
            get { return jointBacker.Length; }
        }

        public Joint this[JointType i]
        {
            get { return jointBacker[(int)i]; }
            set
            {
                if (value.JointType == i)
                {
                    jointBacker[(int)i] = value;
                }
                else
                {
                    throw new ArgumentException("The joint data must be of the type that is being set.");
                }
            }
        }

        public Joint this[int i]
        {
            get
            {
                if (i >= 0 && i < totalJoints)
                {
                    return jointBacker[i];
                }
                else
                {
                    throw new IndexOutOfRangeException();
                }
            }
            set
            {
                if (i >= 0 && i < totalJoints)
                {
                    if (value.JointType == (JointType)i)
                    {
                        jointBacker[i] = value;
                    }
                    else
                    {
                        throw new ArgumentException("The joint type doesn't match.");
                    }
                }
                else
                {
                    throw new IndexOutOfRangeException();
                }
            }
        }
    }
}