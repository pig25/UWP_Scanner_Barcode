using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UWP_Scanner_Barcode
{
    public class BarcodeScannerInfo
    {
        public BarcodeScannerInfo(String deviceName, String deviceId)
        {
            DeviceName = deviceName;
            DeviceId = deviceId;
        }

        public String Name => $"{DeviceName} ({DeviceId})";
        public String DeviceId { get; private set; }
        private string DeviceName;
    }

}
