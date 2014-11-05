﻿namespace GesturalMusic
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Windows;
    using System.Windows.Media;
    using System.Windows.Media.Imaging;
    using Microsoft.Kinect;
    using Ventuz.OSC;
    using System.Windows.Controls;

    /// <summary>
    /// Interaction logic for MainWindow
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        /// <summary>
        /// Radius of drawn hand circles
        /// </summary>
        private const double HandSize = 30;

        /// <summary>
        /// Thickness of drawn joint lines
        /// </summary>
        private const double JointThickness = 3;

        /// <summary>
        /// Thickness of clip edge rectangles
        /// </summary>
        private const double ClipBoundsThickness = 10;

        /// <summary>
        /// Constant for clamping Z values of camera space points from being negative
        /// </summary>
        private const float InferredZPositionClamp = 0.1f;

        /// <summary>
        /// Brush used for drawing hands that are currently tracked as closed
        /// </summary>
        private readonly Brush handClosedBrush = new SolidColorBrush(Color.FromArgb(128, 255, 0, 0));

        /// <summary>
        /// Brush used for drawing hands that are currently tracked as opened
        /// </summary>
        private readonly Brush handOpenBrush = new SolidColorBrush(Color.FromArgb(128, 0, 255, 0));

        /// <summary>
        /// Brush used for drawing hands that are currently tracked as in lasso (pointer) position
        /// </summary>
        private readonly Brush handLassoBrush = new SolidColorBrush(Color.FromArgb(128, 0, 0, 255));

        /// <summary>
        /// Brush used for drawing joints that are currently tracked
        /// </summary>
        private readonly Brush trackedJointBrush = new SolidColorBrush(Color.FromArgb(255, 68, 192, 68));

        /// <summary>
        /// Brush used for drawing joints that are currently inferred
        /// </summary>        
        private readonly Brush inferredJointBrush = Brushes.Yellow;

        /// <summary>
        /// Pen used for drawing bones that are currently inferred
        /// </summary>        
        private readonly Pen inferredBonePen = new Pen(Brushes.Gray, 1);

        /// <summary>
        /// Drawing group for body rendering output
        /// </summary>
        private DrawingGroup drawingGroup;

        /// <summary>
        /// Drawing image that we will display
        /// </summary>
        private DrawingImage imageSource;

        /// <summary>
        /// Active Kinect sensor
        /// </summary>
        private KinectSensor kinectSensor = null;

        /// <summary>
        /// Coordinate mapper to map one type of point to another
        /// </summary>
        private CoordinateMapper coordinateMapper = null;

        /// <summary>
        /// Reader for body frames
        /// </summary>
        private BodyFrameReader bodyFrameReader = null;

        /// <summary>
        /// Array for the bodies
        /// </summary>
        private Body[] bodies = null;

        /// <summary>
        /// definition of bones
        /// </summary>
        private List<Tuple<JointType, JointType>> bones;

        /// <summary>
        /// Width of display (depth space)
        /// </summary>
        private int displayWidth;

        /// <summary>
        /// Height of display (depth space)
        /// </summary>
        private int displayHeight;

        /// <summary>
        /// List of colors for each body tracked
        /// </summary>
        private List<Pen> bodyColors;

        /// <summary>
        /// Current status text to display
        /// </summary>
        private string statusText = null;


        // =========  OSC ===========
        /// <summary>
        /// The host ip address (the computer with Ableton + Max for Live on it). Default: "127.0.0.1"
        /// </summary>
        private String oscHost = "129.21.113.232";

        /// <summary>
        /// The port to send to: default 9001
        /// </summary>
        private int oscPort = 9001;

        /// <summary>
        /// Current status text to display
        /// </summary>
        private UdpWriter osc;
        private UdpWriter oscLocal;
        Random r = new Random();
        private DateTime startTime;


        /// <summary>
        /// A dictionary of Ableton slider controllers.
        /// This will contain elements such as volume and pitch.
        /// The sliders can be fetched by their name (i.e. "instrument/pitch").
        /// </summary>
        Dictionary<string, AbletonSliderController> sliders;

        /// <summary>
        /// A dictionary of Ableton switch controllers.
        /// This will contain elements such as play.
        /// The switches can be fetched by their name (i.e. "instrument/play").
        /// </summary>
        Dictionary<string, AbletonSwitchController> switches;

        string[] instruments;

        /// <summary>
        /// Set the number of partitions 
        /// 
        /// </summary>
        /// 
        /// <param name="sender">object sending the event</param>
        /// <param name="e">event arguments</param>
        private void SetNumPartitions(object sender, RoutedEventArgs e)
        {
            if (onePartition.IsChecked.GetValueOrDefault())        PartitionManager.SetPartitionType(PartitionType.Single);
            else if (twoPartitionLR.IsChecked.GetValueOrDefault()) PartitionManager.SetPartitionType(PartitionType.DoubleLeftRight);
            else if (twoPartitionFB.IsChecked.GetValueOrDefault()) PartitionManager.SetPartitionType(PartitionType.DoubleFrontBack);
            else if (quadPartition.IsChecked.GetValueOrDefault())  PartitionManager.SetPartitionType(PartitionType.Quad);
        }

        /// <summary>
        /// Initializes a new instance of the MainWindow class.
        /// </summary>
        public MainWindow()
        {
            // Get the reference time
            startTime = DateTime.Now;

            ///////////////////////////////////////////////////////////////////////
            // Set up OSC
            ///////////////////////////////////////////////////////////////////////
            osc = new UdpWriter(oscHost, oscPort);
            oscLocal = new UdpWriter("127.0.0.1", 9001);

            ///////////////////////////////////////////////////////////////////////
            // Initialize Ableton controllers
            ///////////////////////////////////////////////////////////////////////
            // Instruments
            instruments = new string[4] { "instr0", "instr1", "instr2", "instr3" };

            // Set up the Ableton slider controllers
            sliders = new Dictionary<string, AbletonSliderController>();
            switches = new Dictionary<string, AbletonSwitchController>();

            for (int i = 0; i < instruments.Length; i++)
            {
                sliders.Add(instruments[i] + "/volume", new AbletonSliderController(oscLocal, instruments[i] + "/volume", 0, 1, true));
                switches.Add(instruments[i] + "/play", new AbletonSwitchController(oscLocal, instruments[i] + "/play"));
            }

            ///////////////////////////////////////////////////////////////////////
            // Initialize Kinect
            ///////////////////////////////////////////////////////////////////////
            try
            {
                this.kinectSensor = KinectSensor.GetDefault();
            }
            catch (Exception e)
            {
                Console.Write(e.StackTrace);
            }

            if (this.kinectSensor != null)
            {
                // get the coordinate mapper
                this.coordinateMapper = this.kinectSensor.CoordinateMapper;

                // get the depth (display) extents
                FrameDescription frameDescription = this.kinectSensor.DepthFrameSource.FrameDescription;

                // get size of joint space
                this.displayWidth = frameDescription.Width;
                this.displayHeight = frameDescription.Height;

                // open the reader for the body frames
                this.bodyFrameReader = this.kinectSensor.BodyFrameSource.OpenReader();

                // a bone defined as a line between two joints
                this.bones = new List<Tuple<JointType, JointType>>();

                // Torso
                this.bones.Add(new Tuple<JointType, JointType>(JointType.Head, JointType.Neck));
                this.bones.Add(new Tuple<JointType, JointType>(JointType.Neck, JointType.SpineShoulder));
                this.bones.Add(new Tuple<JointType, JointType>(JointType.SpineShoulder, JointType.SpineMid));
                this.bones.Add(new Tuple<JointType, JointType>(JointType.SpineMid, JointType.SpineBase));
                this.bones.Add(new Tuple<JointType, JointType>(JointType.SpineShoulder, JointType.ShoulderRight));
                this.bones.Add(new Tuple<JointType, JointType>(JointType.SpineShoulder, JointType.ShoulderLeft));
                this.bones.Add(new Tuple<JointType, JointType>(JointType.SpineBase, JointType.HipRight));
                this.bones.Add(new Tuple<JointType, JointType>(JointType.SpineBase, JointType.HipLeft));

                // Right Arm
                this.bones.Add(new Tuple<JointType, JointType>(JointType.ShoulderRight, JointType.ElbowRight));
                this.bones.Add(new Tuple<JointType, JointType>(JointType.ElbowRight, JointType.WristRight));
                this.bones.Add(new Tuple<JointType, JointType>(JointType.WristRight, JointType.HandRight));
                this.bones.Add(new Tuple<JointType, JointType>(JointType.HandRight, JointType.HandTipRight));
                this.bones.Add(new Tuple<JointType, JointType>(JointType.WristRight, JointType.ThumbRight));

                // Left Arm
                this.bones.Add(new Tuple<JointType, JointType>(JointType.ShoulderLeft, JointType.ElbowLeft));
                this.bones.Add(new Tuple<JointType, JointType>(JointType.ElbowLeft, JointType.WristLeft));
                this.bones.Add(new Tuple<JointType, JointType>(JointType.WristLeft, JointType.HandLeft));
                this.bones.Add(new Tuple<JointType, JointType>(JointType.HandLeft, JointType.HandTipLeft));
                this.bones.Add(new Tuple<JointType, JointType>(JointType.WristLeft, JointType.ThumbLeft));

                // Right Leg
                this.bones.Add(new Tuple<JointType, JointType>(JointType.HipRight, JointType.KneeRight));
                this.bones.Add(new Tuple<JointType, JointType>(JointType.KneeRight, JointType.AnkleRight));
                this.bones.Add(new Tuple<JointType, JointType>(JointType.AnkleRight, JointType.FootRight));

                // Left Leg
                this.bones.Add(new Tuple<JointType, JointType>(JointType.HipLeft, JointType.KneeLeft));
                this.bones.Add(new Tuple<JointType, JointType>(JointType.KneeLeft, JointType.AnkleLeft));
                this.bones.Add(new Tuple<JointType, JointType>(JointType.AnkleLeft, JointType.FootLeft));

                // populate body colors, one for each BodyIndex
                this.bodyColors = new List<Pen>();

                this.bodyColors.Add(new Pen(Brushes.Red, 6));
                this.bodyColors.Add(new Pen(Brushes.Orange, 6));
                this.bodyColors.Add(new Pen(Brushes.Green, 6));
                this.bodyColors.Add(new Pen(Brushes.Blue, 6));
                this.bodyColors.Add(new Pen(Brushes.Indigo, 6));
                this.bodyColors.Add(new Pen(Brushes.Violet, 6));

                // set IsAvailableChanged event notifier
                this.kinectSensor.IsAvailableChanged += this.Sensor_IsAvailableChanged;

                // open the sensor
                this.kinectSensor.Open();

                // set the status text
                this.StatusText = this.kinectSensor.IsAvailable ? Properties.Resources.RunningStatusText
                                                                : Properties.Resources.NoSensorStatusText;

                // Create the drawing group we'll use for drawing
                this.drawingGroup = new DrawingGroup();

                // Create an image source that we can use in our image control
                this.imageSource = new DrawingImage(this.drawingGroup);

                // use the window object as the view model in this simple example
                this.DataContext = this;

                // initialize the components (controls) of the window
                this.InitializeComponent();
            }
        }

        /// <summary>
        /// INotifyPropertyChangedPropertyChanged event to allow window controls to bind to changeable data
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Gets the bitmap to display
        /// </summary>
        public ImageSource ImageSource
        {
            get
            {
                return this.imageSource;
            }
        }

        /// <summary>
        /// Execute start up tasks
        /// </summary>
        /// <param name="sender">object sending the event</param>
        /// <param name="e">event arguments</param>
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (this.bodyFrameReader != null)
            {
                this.bodyFrameReader.FrameArrived += this.Reader_FrameArrived;
            }

        }

        /// <summary>
        /// Execute shutdown tasks
        /// </summary>
        /// <param name="sender">object sending the event</param>
        /// <param name="e">event arguments</param>
        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            if (this.bodyFrameReader != null)
            {
                // BodyFrameReader is IDisposable
                this.bodyFrameReader.Dispose();
                this.bodyFrameReader = null;
            }

            if (this.kinectSensor != null)
            {
                this.kinectSensor.Close();
                this.kinectSensor = null;
            }
        }

        /// <summary>
        /// Handles the body frame data arriving from the sensor
        /// </summary>
        /// <param name="sender">object sending the event</param>
        /// <param name="e">event arguments</param>
        private void Reader_FrameArrived(object sender, BodyFrameArrivedEventArgs e)
        {
            bool dataReceived = false;

            using (BodyFrame bodyFrame = e.FrameReference.AcquireFrame())
            {
                if (bodyFrame != null)
                {
                    if (this.bodies == null)
                    {
                        this.bodies = new Body[bodyFrame.BodyCount];
                    }

                    // The first time GetAndRefreshBodyData is called, Kinect will allocate each Body in the array.
                    // As long as those body objects are not disposed and not set to null in the array,
                    // those body objects will be re-used.
                    bodyFrame.GetAndRefreshBodyData(this.bodies);
                    dataReceived = true;
                }
            }

            if (dataReceived)
            {
                Update();
            }
        }

        private void Update()
        {
            SolidColorBrush bgColor = SendInstrumentData();

            ///////////////////////////////////////////////////////////////////////
            // Draw the Screen
            ///////////////////////////////////////////////////////////////////////
            using (DrawingContext dc = this.drawingGroup.Open())
            {
                dc.DrawRectangle(bgColor, null, new Rect(0.0, 0.0, this.displayWidth, this.displayHeight));

                // Crosshairs so the user can know where positive/negative are for each limb
                dc.DrawLine(new Pen(Brushes.Red, 2.0), new Point(this.displayWidth / 2, 0.0), new Point(this.displayWidth / 2, this.displayHeight));
                dc.DrawLine(new Pen(Brushes.Red, 2.0), new Point(0.0, this.displayHeight / 2), new Point(this.displayWidth, this.displayHeight / 2));

                int penIndex = 0;
                foreach (Body body in this.bodies)
                {
                    Pen drawPen = this.bodyColors[penIndex++];

                    if (body.IsTracked)
                    {
                        this.DrawClippedEdges(body, dc);

                        IReadOnlyDictionary<JointType, Joint> joints = body.Joints;

                        // convert the joint points to depth (display) space
                        Dictionary<JointType, Point> jointPoints = new Dictionary<JointType, Point>();

                        foreach (JointType jointType in joints.Keys)
                        {
                            // sometimes the depth(Z) of an inferred joint may show as negative
                            // clamp down to 0.1f to prevent coordinatemapper from returning (-Infinity, -Infinity)
                            CameraSpacePoint position = joints[jointType].Position;
                            if (position.Z < 0)
                            {
                                position.Z = InferredZPositionClamp;
                            }

                            DepthSpacePoint depthSpacePoint = this.coordinateMapper.MapCameraPointToDepthSpace(position);
                            jointPoints[jointType] = new Point(depthSpacePoint.X, depthSpacePoint.Y);
                        }

                        this.DrawBody(joints, jointPoints, dc, drawPen);

                        this.DrawHand(body.HandLeftState, jointPoints[JointType.HandLeft], dc);
                        this.DrawHand(body.HandRightState, jointPoints[JointType.HandRight], dc);
                    }
                }

                // prevent drawing outside of our render area
                this.drawingGroup.ClipGeometry = new RectangleGeometry(new Rect(0.0, 0.0, this.displayWidth, this.displayHeight));
            }
        }


        /// <summary>
        /// Sends OSC messages if applicable
        /// </summary>
        /// <returns>The color the background should display (for user feedback)</returns>
        private SolidColorBrush SendInstrumentData()
        {
            // Selects the first body that is tracked and use that for our calculations
            Body b = System.Linq.Enumerable.FirstOrDefault(this.bodies, bod => bod.IsTracked);
            if (b == null) return Brushes.Black;

            // Send joint data to animators, write to a file
            sendJointData(b, true);

            CameraSpacePoint spineMidPos = b.Joints[JointType.SpineMid].Position;
            CameraSpacePoint lHandPos = b.Joints[JointType.HandLeft].Position;
            CameraSpacePoint rHandPos = b.Joints[JointType.HandRight].Position;

            // trigger start if both left and right hand are open
            bool triggerStart = b.HandLeftState == b.HandRightState && b.HandLeftState == HandState.Open;
            // trigger end if both left and right hand are closed and below the Kinect
            bool triggerEnd = b.HandLeftState == b.HandRightState && b.HandLeftState == HandState.Closed && lHandPos.Y < 0 && rHandPos.Y < 0;

            int partition = PartitionManager.GetPartition(spineMidPos);

            if (triggerStart)
            {
                switches[instruments[partition] + "/play"].SwitchOn();
            }
            else if (triggerEnd)
            {
                switches[instruments[partition] + "/play"].SwitchOff();
            }

            if (b.HandLeftState == HandState.Lasso)
            {
                // Send volume as a value between 0 and 1, only when thumbs up
                sliders[instruments[partition] + "/volume"].Send(lHandPos.Y);
            }

            // TODO: Move this from this function
            // If we detect either a trigger to start or stop the track, change the background color
            if (triggerStart || triggerEnd)
            {
                return Brushes.LightGray;
            }
            else if (spineMidPos.Z > KinectStageArea.GetCenterZ())
            {
                return Brushes.DarkGray;
            }
            else
            {
                return Brushes.Black;
            }
        }

        /// <summary>
        /// Sends joint data to the animators. If the boolean writeToFile is set, it will
        /// generate a file locally of animation data.
        /// TODO: This method only writes to file and does not send over OSC
        /// </summary>
        /// <param name="body">The body of joints to send</param>
        /// <param name="writeToFile">If we should write to a file (currently ignored)</param>
        private void sendJointData(Body body, bool writeToFile)
        {
            System.IO.StreamWriter file = new System.IO.StreamWriter("jointOutput.csv", true);

            TimeSpan elapsedTime = DateTime.Now - startTime;

            foreach (JointType jointType in Enum.GetValues(typeof(JointType)))
            {
                Joint joint = body.Joints[jointType];
                file.WriteLine(joint.JointType + "," + joint.Position.X + "," + joint.Position.Y + "," + joint.Position.Z + "," + elapsedTime.Milliseconds.ToString() + "\n");
            }

            file.Close();
        }

        /// <summary>
        /// Draws a body
        /// </summary>
        /// <param name="joints">joints to draw</param>
        /// <param name="jointPoints">translated positions of joints to draw</param>
        /// <param name="drawingContext">drawing context to draw to</param>
        /// <param name="drawingPen">specifies color to draw a specific body</param>
        private void DrawBody(IReadOnlyDictionary<JointType, Joint> joints, IDictionary<JointType, Point> jointPoints, DrawingContext drawingContext, Pen drawingPen)
        {
            // Draw the bones
            foreach (var bone in this.bones)
            {
                this.DrawBone(joints, jointPoints, bone.Item1, bone.Item2, drawingContext, drawingPen);
            }

            // Draw the joints
            foreach (JointType jointType in joints.Keys)
            {
                Brush drawBrush = null;

                TrackingState trackingState = joints[jointType].TrackingState;

                if (trackingState == TrackingState.Tracked)
                {
                    drawBrush = this.trackedJointBrush;
                }
                else if (trackingState == TrackingState.Inferred)
                {
                    drawBrush = this.inferredJointBrush;
                }

                if (drawBrush != null)
                {
                    drawingContext.DrawEllipse(drawBrush, null, jointPoints[jointType], JointThickness, JointThickness);
                }
            }
        }

        /// <summary>
        /// Draws one bone of a body (joint to joint)
        /// </summary>
        /// <param name="joints">joints to draw</param>
        /// <param name="jointPoints">translated positions of joints to draw</param>
        /// <param name="jointType0">first joint of bone to draw</param>
        /// <param name="jointType1">second joint of bone to draw</param>
        /// <param name="drawingContext">drawing context to draw to</param>
        /// /// <param name="drawingPen">specifies color to draw a specific bone</param>
        private void DrawBone(IReadOnlyDictionary<JointType, Joint> joints, IDictionary<JointType, Point> jointPoints, JointType jointType0, JointType jointType1, DrawingContext drawingContext, Pen drawingPen)
        {
            Joint joint0 = joints[jointType0];
            Joint joint1 = joints[jointType1];

            // If we can't find either of these joints, exit
            if (joint0.TrackingState == TrackingState.NotTracked ||
                joint1.TrackingState == TrackingState.NotTracked)
            {
                return;
            }

            // We assume all drawn bones are inferred unless BOTH joints are tracked
            Pen drawPen = this.inferredBonePen;
            if ((joint0.TrackingState == TrackingState.Tracked) && (joint1.TrackingState == TrackingState.Tracked))
            {
                drawPen = drawingPen;
            }

            drawingContext.DrawLine(drawPen, jointPoints[jointType0], jointPoints[jointType1]);
        }

        /// <summary>
        /// Draws a hand symbol if the hand is tracked: red circle = closed, green circle = opened; blue circle = lasso
        /// </summary>
        /// <param name="handState">state of the hand</param>
        /// <param name="handPosition">position of the hand</param>
        /// <param name="drawingContext">drawing context to draw to</param>
        private void DrawHand(HandState handState, Point handPosition, DrawingContext drawingContext)
        {
            switch (handState)
            {
                case HandState.Closed:
                    drawingContext.DrawEllipse(this.handClosedBrush, null, handPosition, HandSize, HandSize);
                    break;

                case HandState.Open:
                    drawingContext.DrawEllipse(this.handOpenBrush, null, handPosition, HandSize, HandSize);
                    break;

                case HandState.Lasso:
                    drawingContext.DrawEllipse(this.handLassoBrush, null, handPosition, HandSize, HandSize);
                    break;
            }
        }

        /// <summary>
        /// Draws indicators to show which edges are clipping body data
        /// </summary>
        /// <param name="body">body to draw clipping information for</param>
        /// <param name="drawingContext">drawing context to draw to</param>
        private void DrawClippedEdges(Body body, DrawingContext drawingContext)
        {
            FrameEdges clippedEdges = body.ClippedEdges;

            if (clippedEdges.HasFlag(FrameEdges.Bottom))
            {
                drawingContext.DrawRectangle(
                    Brushes.Red,
                    null,
                    new Rect(0, this.displayHeight - ClipBoundsThickness, this.displayWidth, ClipBoundsThickness));
            }

            if (clippedEdges.HasFlag(FrameEdges.Top))
            {
                drawingContext.DrawRectangle(
                    Brushes.Red,
                    null,
                    new Rect(0, 0, this.displayWidth, ClipBoundsThickness));
            }

            if (clippedEdges.HasFlag(FrameEdges.Left))
            {
                drawingContext.DrawRectangle(
                    Brushes.Red,
                    null,
                    new Rect(0, 0, ClipBoundsThickness, this.displayHeight));
            }

            if (clippedEdges.HasFlag(FrameEdges.Right))
            {
                drawingContext.DrawRectangle(
                    Brushes.Red,
                    null,
                    new Rect(this.displayWidth - ClipBoundsThickness, 0, ClipBoundsThickness, this.displayHeight));
            }
        }
    }
}
