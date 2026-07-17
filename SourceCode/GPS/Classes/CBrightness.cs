using System;
using System.Management;

namespace AgOpenGPS
{
    // Punto de aislamiento de brillo de pantalla (traspaso portabilidad
    // 2026-07-17): FormGPS habla solo con esta interfaz. En un port se
    // reemplaza la implementación WMI por la API de la plataforma
    // (sysfs backlight en Linux, WindowManager en Android, etc.).
    public interface IBrightnessController
    {
        bool IsSupported { get; }

        void BrightnessIncrease();

        void BrightnessDecrease();

        int GetBrightness();

        void SetBrightness(int bright);
    }

    // Implementación Windows vía WMI (System.Management).
    //Class written and inspired by Andy
    public class CWindowsSettingsBrightnessController : IBrightnessController
    {
        public bool IsSupported { get; private set; }

        public CWindowsSettingsBrightnessController(bool isOn)
        {
            IsSupported = isOn && Get() != -1;
        }

        private int Get()
        {
            try // this will fail if not a device with controllable brightness (eg, a desktop)
            {
                var mclass = new ManagementClass("WmiMonitorBrightness")
                {
                    Scope = new ManagementScope(@"\\.\root\wmi")
                };
                var instances = mclass.GetInstances();
                foreach (ManagementObject instance in instances)
                {
                    return (byte)instance.GetPropertyValue("CurrentBrightness");
                }
                return 0;
            }
            catch
            {
                return -1; // check this and disable the buttons if not available
            }
        }

        private void Set(int brightness)
        {
            try // and so will this
            {
                var mclass = new ManagementClass("WmiMonitorBrightnessMethods")
                {
                    Scope = new ManagementScope(@"\\.\root\wmi")
                };
                var instances = mclass.GetInstances();
                var args = new object[] { 1, brightness };
                foreach (ManagementObject instance in instances)
                {
                    instance.InvokeMethod("WmiSetBrightness", args);
                }
            }
            catch
            {
                // meh, don't care
            }
        }

        public void BrightnessIncrease()
        {
            if (IsSupported) Set(Math.Min(100, Get() + 10));
        }

        public void BrightnessDecrease()
        {
            if (IsSupported) Set(Math.Max(10, Get() - 10));
        }

        public int GetBrightness()
        {
            if (IsSupported) return Get();
            else return -1;
        }

        public void SetBrightness(int bright)
        {
            if (IsSupported) Set(bright);
        }
    }
}
