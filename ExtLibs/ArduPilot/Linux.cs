﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MissionPlanner.Utilities
{
    /// <summary>
    /// USB serial device information on Linux (sysfs) and macOS (IOKit registry),
    /// the equivalent of the Windows device manager query.
    /// </summary>
    public class Linux
    {
        public static List<ArduPilot.DeviceInfo> GetAllCOMPorts()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return GetAllCOMPortsMac();

            List<ArduPilot.DeviceInfo> ans = new List<ArduPilot.DeviceInfo>();
            //var proc = System.Diagnostics.Process.Start("bash", @"-c ""lsusb -tv > /tmp/lsusb.list""");
            //proc.WaitForExit();


            try
            {
                if (!File.Exists("/usr/bin/bash"))
                    return ans;

                var proc = System.Diagnostics.Process.Start("/usr/bin/bash", @"-c ""/usr/bin/find /sys/bus/usb/devices/usb*/ -name dev | /usr/bin/grep tty > /tmp/usb.list""");
                proc.WaitForExit();

                var data = File.ReadAllLines("/tmp/usb.list");
                Console.WriteLine(data);
                foreach (var device in data)
                {
                    try
                    {
                        var pth = Path.Combine(Path.GetDirectoryName(device), "../../../");
                        var product = File.ReadAllText(pth + "product").TrimEnd();

                        ans.Add(new ArduPilot.DeviceInfo()
                        {
                            board = product,
                            description = product,
                            name = product,
                            // same form as Windows (USB\VID_xxxx&PID_xxxx&REV_xxxx) so APFirmware matches it
                            hardwareid = $"USB\\VID_{File.ReadAllText(pth + "idVendor").TrimEnd()}&PID_{File.ReadAllText(pth + "idProduct").TrimEnd()}&REV_0000"
                        });
                    } catch (Exception ex) { Console.WriteLine(ex.ToString()); }
                }
            }
            catch (Exception ex) { Console.WriteLine(ex.ToString()); }

            return ans;
        }

        /// <summary>
        /// Parse the IOKit registry: each USB device's product name and VID/PID,
        /// with the /dev/cu.* serial device of its CDC/serial interface.
        /// </summary>
        public static List<ArduPilot.DeviceInfo> GetAllCOMPortsMac()
        {
            var ans = new List<ArduPilot.DeviceInfo>();
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("/usr/sbin/ioreg", "-r -c IOUSBHostDevice -l -w0")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };
                string output;
                using (var proc = System.Diagnostics.Process.Start(psi))
                {
                    output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(10000);
                }
                ans.AddRange(ParseIoreg(output));
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }

            return ans;
        }

        private class IoregNode
        {
            public int Depth;
            public bool IsUsbDevice;
            public string Product, Vendor;
            public int Vid = -1, Pid = -1;
        }

        public static List<ArduPilot.DeviceInfo> ParseIoreg(string output)
        {
            var ans = new List<ArduPilot.DeviceInfo>();
            var seen = new HashSet<string>();
            var property = new Regex("^[\\s|]*\"([^\"]+)\" = (.*)$");
            // the chain of registry nodes from the root to the current one
            var stack = new List<IoregNode>();

            foreach (var raw in output.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                var node = line.IndexOf("+-o ", StringComparison.Ordinal);
                if (node >= 0 && line.Substring(0, node).Trim(' ', '|').Length == 0)
                {
                    while (stack.Count > 0 && stack[stack.Count - 1].Depth >= node)
                        stack.RemoveAt(stack.Count - 1);
                    stack.Add(new IoregNode
                    {
                        Depth = node,
                        IsUsbDevice = line.Contains("<class IOUSBHostDevice") || line.Contains("<class IOUSBDevice")
                    });
                    continue;
                }

                var m = property.Match(line);
                if (!m.Success || stack.Count == 0)
                    continue;
                var current = stack[stack.Count - 1];
                var key = m.Groups[1].Value;
                var value = m.Groups[2].Value.Trim();
                int number;

                switch (key)
                {
                    case "USB Product Name":
                    case "kUSBProductString":
                        if (current.Product == null)
                            current.Product = value.Trim('"');
                        break;
                    case "USB Vendor Name":
                    case "kUSBVendorString":
                        if (current.Vendor == null)
                            current.Vendor = value.Trim('"');
                        break;
                    case "idVendor":
                        if (current.Vid < 0 && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                            current.Vid = number;
                        break;
                    case "idProduct":
                        if (current.Pid < 0 && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                            current.Pid = number;
                        break;
                    case "IOCalloutDevice":
                        var dev = value.Trim('"');
                        // the closest USB device above the serial port node
                        IoregNode usb = null;
                        for (int i = stack.Count - 1; i >= 0 && usb == null; i--)
                            if (stack[i].IsUsbDevice)
                                usb = stack[i];
                        if (usb == null || usb.Vid < 0 || usb.Pid < 0 || !seen.Add(dev))
                            break;
                        var name = usb.Product ?? usb.Vendor ?? dev;
                        ans.Add(new ArduPilot.DeviceInfo()
                        {
                            name = dev,
                            board = name,
                            description = name + " (" + dev + ")",
                            hardwareid = string.Format(CultureInfo.InvariantCulture, "USB\\VID_{0:X4}&PID_{1:X4}&REV_0000", usb.Vid, usb.Pid)
                        });
                        break;
                }
            }

            return ans;
        }
    }
}
