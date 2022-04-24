using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Windows.Devices.Enumeration;
using Windows.Devices.PointOfService;
using System.Collections.ObjectModel;
using Windows.UI.Core;
using System.Threading.Tasks;
using Windows.UI.Popups;
using System.ComponentModel;
using Windows.Media.Capture;
using Windows.System.Display;
using Windows.Storage.Streams;
using Windows.Security.Cryptography;
using Windows.Graphics.Display;

// 空白頁項目範本已記錄在 https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x404

namespace UWP_Scanner_Barcode
{
    /// <summary>
    /// 可以在本身使用或巡覽至框架內的空白頁面。
    /// </summary>
    public sealed partial class MainPage : Page, INotifyPropertyChanged
    {

        public bool IsScannerClaimed { get; set; } = false;
        public bool IsPreviewing { get; set; } = false;
        public bool ScannerSupportsPreview { get; set; } = false;
        public bool SoftwareTriggerStarted { get; set; } = false;

        //MainPage rootPage = MainPage.Current;
        ObservableCollection<BarcodeScannerInfo> barcodeScanners = new ObservableCollection<BarcodeScannerInfo>();
        BarcodeScanner selectedScanner = null;
        ClaimedBarcodeScanner claimedScanner = null;
        DeviceWatcher watcher;

        static readonly Guid rotationGuid = new Guid("C380465D-2271-428C-9B83-ECEA3B4A85C1");

        DisplayRequest displayRequest = new DisplayRequest();
        MediaCapture mediaCapture;

        bool isSelectionChanging = false;
        string pendingSelectionDeviceId = null;
        bool isStopPending = false;

        public event PropertyChangedEventHandler PropertyChanged;

        public MainPage()
        {
            this.InitializeComponent();

            //ScannerListSource.Source = barcodeScanners;
            DataContext = barcodeScanners;
            watcher = DeviceInformation.CreateWatcher(BarcodeScanner.GetDeviceSelector());
            watcher.Added += Watcher_Added;
            watcher.Removed += Watcher_Removed;
            watcher.Updated += Watcher_Updated;
            watcher.Start();

            DataContext = this;
        }

        private async void Watcher_Added(DeviceWatcher sender, DeviceInformation args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                barcodeScanners.Add(new BarcodeScannerInfo(args.Name, args.Id));

                // Select the first scanner by default.
                if (barcodeScanners.Count == 1)
                {
                    ScannerListBox.SelectedIndex = 0;
                }
            });
        }

        private async void Watcher_Removed(DeviceWatcher sender, DeviceInformationUpdate args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                for (int i = barcodeScanners.Count - 1; i >= 0; i--)
                {
                    if (barcodeScanners[i].DeviceId == args.Id)
                    {
                        barcodeScanners.RemoveAt(i);
                    }
                }
            });
        }

        private void Watcher_Updated(DeviceWatcher sender, DeviceInformationUpdate args)
        {
            // We don't do anything here, but this event needs to be handled to enable realtime updates.
            //See https://aka.ms/devicewatcher_added.
        }



        /// <summary>
        /// Starts previewing the selected scanner's video feed and prevents the display from going to sleep.
        /// </summary>
        private async Task StartMediaCaptureAsync(string videoDeviceId)
        {
            mediaCapture = new MediaCapture();

            // Register for a notification when something goes wrong
            mediaCapture.Failed += MediaCapture_Failed;

            var settings = new MediaCaptureInitializationSettings
            {
                VideoDeviceId = videoDeviceId,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly,
            };

            // Initialize MediaCapture
            bool captureInitialized = false;
            try
            {
                await mediaCapture.InitializeAsync(settings);
                captureInitialized = true;
            }
            catch (UnauthorizedAccessException)
            {
                //rootPage.NotifyUser("The app was denied access to the camera", NotifyType.ErrorMessage);
            }
            catch (Exception e)
            {
                //rootPage.NotifyUser("Failed to initialize the camera: " + e.Message, NotifyType.ErrorMessage);
            }

            if (captureInitialized)
            {
                // Prevent the device from sleeping while the preview is running.
                displayRequest.RequestActive();

                PreviewControl.Source = mediaCapture;
                await mediaCapture.StartPreviewAsync();
                await SetPreviewRotationAsync(DisplayInformation.GetForCurrentView().CurrentOrientation);
                IsPreviewing = true;
                RaisePropertyChanged(nameof(IsPreviewing));
            }
            else
            {
                mediaCapture.Dispose();
                mediaCapture = null;
            }
        }

        /// <summary>
        /// Close the scanners and stop the preview.
        /// </summary>
        private async Task CloseScannerResourcesAsync()
        {
            claimedScanner?.Dispose();
            claimedScanner = null;

            selectedScanner?.Dispose();
            selectedScanner = null;

            SoftwareTriggerStarted = false;
            RaisePropertyChanged(nameof(SoftwareTriggerStarted));

            if (IsPreviewing)
            {
                if (mediaCapture != null)
                {
                    await mediaCapture.StopPreviewAsync();
                    mediaCapture.Dispose();
                    mediaCapture = null;
                }

                // Allow the display to go to sleep.
                displayRequest.RequestRelease();

                IsPreviewing = false;
                RaisePropertyChanged(nameof(IsPreviewing));
            }
        }

        /// <summary>
        /// Set preview rotation and mirroring state to adjust for the orientation of the camera, and for embedded cameras, the rotation of the device.
        /// </summary>
        /// <param name="message"></param>
        /// <param name="type"></param>
        private async Task SetPreviewRotationAsync(DisplayOrientations displayOrientation)
        {
            bool isExternalCamera;
            bool isPreviewMirrored;

            // Figure out where the camera is located to account for mirroring and later adjust rotation accordingly.
            DeviceInformation cameraInformation = await DeviceInformation.CreateFromIdAsync(selectedScanner.VideoDeviceId);

            if ((cameraInformation.EnclosureLocation == null) || (cameraInformation.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Unknown))
            {
                isExternalCamera = true;
                isPreviewMirrored = false;
            }
            else
            {
                isExternalCamera = false;
                isPreviewMirrored = (cameraInformation.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Front);
            }

            PreviewControl.FlowDirection = isPreviewMirrored ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

            if (!isExternalCamera)
            {
                // Calculate which way and how far to rotate the preview.
                int rotationDegrees = 0;
                switch (displayOrientation)
                {
                    case DisplayOrientations.Portrait:
                        rotationDegrees = 90;
                        break;
                    case DisplayOrientations.LandscapeFlipped:
                        rotationDegrees = 180;
                        break;
                    case DisplayOrientations.PortraitFlipped:
                        rotationDegrees = 270;
                        break;
                    case DisplayOrientations.Landscape:
                    default:
                        rotationDegrees = 0;
                        break;
                }

                // The rotation direction needs to be inverted if the preview is being mirrored.
                if (isPreviewMirrored)
                {
                    rotationDegrees = (360 - rotationDegrees) % 360;
                }

                // Add rotation metadata to the preview stream to make sure the aspect ratio / dimensions match when rendering and getting preview frames.
                var streamProperties = mediaCapture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview);
                streamProperties.Properties[rotationGuid] = rotationDegrees;
                await mediaCapture.SetEncodingPropertiesAsync(MediaStreamType.VideoPreview, streamProperties, null);
            }
        }

        /// <summary>
        /// Media capture failed, potentially due to the camera being unplugged.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="errorEventArgs"></param>
        private void MediaCapture_Failed(MediaCapture sender, MediaCaptureFailedEventArgs errorEventArgs)
        {
            //rootPage.NotifyUser("Media capture failed. Make sure the camera is still connected.", NotifyType.ErrorMessage);
        }

        /// <summary>
        /// Event Handler for Show Preview Button Click.
        /// Displays the preview window for the selected barcode scanner.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void Btn_ShowPreview_Click(object sender, RoutedEventArgs e)
        {
            await claimedScanner?.ShowVideoPreviewAsync();
        }

        /// <summary>
        /// Event Handler for Hide Preview Button Click.
        /// Hides the preview window for the selected barcode scanner.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Btn_HidePreview_Click(object sender, RoutedEventArgs e)
        {
            claimedScanner?.HideVideoPreview();
        }

        /// <summary>
        /// Event Handler for Start Software Trigger Button Click.
        /// Starts scanning.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void Btn_OpenScanner_Click(object sender, RoutedEventArgs e)
        {
            if (claimedScanner != null)
            {
                await claimedScanner.StartSoftwareTriggerAsync();

                SoftwareTriggerStarted = true;
                RaisePropertyChanged(nameof(SoftwareTriggerStarted));
            }
        }

        /// <summary>
        /// Event Handler for Stop Software Trigger Button Click.
        /// Stops scanning.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void Btn_CloseScanner_Click(object sender, RoutedEventArgs e)
        {
            if (claimedScanner != null)
            {
                await claimedScanner.StopSoftwareTriggerAsync();
                SoftwareTriggerStarted = false;
                RaisePropertyChanged(nameof(SoftwareTriggerStarted));
            }
        }



        /// <summary>
        /// Event handler for scanner listbox selection changed
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private async void ScannerListBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            var selectedScannerInfo = (BarcodeScannerInfo)args.AddedItems[0];
            var deviceId = selectedScannerInfo.DeviceId;

            if (isSelectionChanging)
            {
                pendingSelectionDeviceId = deviceId;
                return;
            }

            do
            {
                await SelectScannerAsync(deviceId);

                // Stop takes precedence over updating the selection.
                if (isStopPending)
                {
                    await CloseScannerResourcesAsync();
                    break;
                }

                deviceId = pendingSelectionDeviceId;
                pendingSelectionDeviceId = null;
            } while (!String.IsNullOrEmpty(deviceId));
        }

        /// <summary>
        /// Select the scanner specified by its device ID.
        /// </summary>
        /// <param name="scannerDeviceId"></param>
        private async Task SelectScannerAsync(string scannerDeviceId)
        {
            try
            {
         isSelectionChanging = true;

            await CloseScannerResourcesAsync();

            selectedScanner = await BarcodeScanner.FromIdAsync(scannerDeviceId);

            if (selectedScanner != null)
            {
                claimedScanner = await selectedScanner.ClaimScannerAsync();
                if (claimedScanner != null)
                {
                        //code39=137, code128 = 144 . qrcode = 168
                        var symbologyList = new List<uint>() { 137, 144, 168 };
                        await claimedScanner.SetActiveSymbologiesAsync(symbologyList);
                        await claimedScanner.EnableAsync();
                    claimedScanner.Closed += ClaimedScanner_Closed;
                    ScannerSupportsPreview = !String.IsNullOrEmpty(selectedScanner.VideoDeviceId);
                    RaisePropertyChanged(nameof(ScannerSupportsPreview));

                    claimedScanner.DataReceived += ClaimedScanner_DataReceived;

                    if (ScannerSupportsPreview)
                    {
                        await StartMediaCaptureAsync(selectedScanner.VideoDeviceId);
                    }
                }
                else
                {

                    //rootPage.NotifyUser("Failed to claim the selected barcode scanner", NotifyType.ErrorMessage);
                }

            }
            else
            {
                //rootPage.NotifyUser("Failed to create a barcode scanner object", NotifyType.ErrorMessage);
            }

            IsScannerClaimed = claimedScanner != null;
            RaisePropertyChanged(nameof(IsScannerClaimed));

            isSelectionChanging = false;
            }
            catch(Exception ex)
            {
                Console.WriteLine(ex.Message.ToString());
                return;
            }
   
        }

        /// <summary>
        /// Closed notification was received from the selected scanner.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private void ClaimedScanner_Closed(ClaimedBarcodeScanner sender, ClaimedBarcodeScannerClosedEventArgs args)
        {
            // Resources associated to the claimed barcode scanner can be cleaned up here
        }

        /// <summary>
        /// Scan data was received from the selected scanner.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private async void ClaimedScanner_DataReceived(ClaimedBarcodeScanner sender, BarcodeScannerDataReceivedEventArgs args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                ScenarioOutputScanDataLabel.Text = GetDataLabelString(args.Report.ScanDataLabel, args.Report.ScanDataType);
                ScenarioOutputScanData.Text = GetDataString(args.Report.ScanData);
                ScenarioOutputScanDataType.Text = BarcodeSymbologies.GetName(args.Report.ScanDataType);
            });
        }

        /// <summary>
        /// Update listeners that a property was changed so that data bindings can be updated.
        /// </summary>
        /// <param name="propertyName"></param>
        public void RaisePropertyChanged(string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public static string GetDataLabelString(IBuffer data, uint scanDataType)
        {
            // Only certain data types contain encoded text.
            // To keep this simple, we'll just decode a few of them.
            if (data == null)
            {
                return "No data";
            }

            // This is not an exhaustive list of symbologies that can be converted to a string.
            if (scanDataType == BarcodeSymbologies.Upca ||
                scanDataType == BarcodeSymbologies.UpcaAdd2 ||
                scanDataType == BarcodeSymbologies.UpcaAdd5 ||
                scanDataType == BarcodeSymbologies.Upce ||
                scanDataType == BarcodeSymbologies.UpceAdd2 ||
                scanDataType == BarcodeSymbologies.UpceAdd5 ||
                scanDataType == BarcodeSymbologies.Ean8 ||
                scanDataType == BarcodeSymbologies.TfStd ||
                scanDataType == BarcodeSymbologies.OcrA ||
                scanDataType == BarcodeSymbologies.OcrB ||
                  scanDataType == BarcodeSymbologies.Code128 ||
                    scanDataType == BarcodeSymbologies.Qr)
            {
                // The UPC, EAN8, and 2 of 5 families encode the digits 0..9
                // which are then sent to the app in a UTF8 string (like "01234").
                return CryptographicBuffer.ConvertBinaryToString(BinaryStringEncoding.Utf8, data);
            }

            // Some other symbologies (typically 2-D symbologies) contain binary data that
            // should not be converted to text.
            return string.Format("Decoded data unavailable. Raw label data: {0}", GetDataString(data));
        }
        public static string GetDataString(IBuffer data)
        {
            if (data == null)
            {
                return "No data";
            }

            // Just to show that we have the raw data, we'll print the value of the bytes.
            // Arbitrarily limit the number of bytes printed to 20 so the UI isn't overloaded.
            string result = CryptographicBuffer.EncodeToHexString(data);
            if (result.Length > 40)
            {
                result = result.Substring(0, 40) + "...";
            }
            return result;
        }
    }
}
