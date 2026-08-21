using System;
using System.Management;
using System.Security.Cryptography;
using System.Text;

namespace DarkVisualsLauncher1.Security
{
    /// <summary>
    /// Раньше HWID = один Win32_Processor.ProcessorId. Проблема: на многих
    /// виртуалках/облачных инстансах он одинаковый или пустой, и это легко
    /// узнать заранее и подделать через WMI-фильтры. Здесь HWID собирается
    /// из нескольких аппаратных источников и хэшируется — подделать все
    /// сразу заметно сложнее, чем один параметр.
    /// </summary>
    internal static class HwidHelper
    {
        public static string GetHwid()
        {
            string cpu = QueryWmi("Win32_Processor", "ProcessorId");
            string board = QueryWmi("Win32_BaseBoard", "SerialNumber");
            string disk = QueryWmi("Win32_DiskDrive", "SerialNumber");

            // Если WMI вообще недоступен (бывает в некоторых окружениях/антивирусах,
            // блокирующих WMI) — не даём всем таким машинам одинаковый HWID "unknown".
            if (cpu == "unknown" && board == "unknown" && disk == "unknown")
            {
                cpu = Environment.MachineName;
                board = Environment.UserName;
            }

            string raw = $"{cpu}|{board}|{disk}";

            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(hash); // стабильная 64-символьная строка для этой машины
        }

        private static string QueryWmi(string wmiClass, string property)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher($"SELECT {property} FROM {wmiClass}");
                foreach (var o in searcher.Get())
                {
                    string? val = o[property]?.ToString();
                    if (!string.IsNullOrWhiteSpace(val))
                        return val.Trim();
                }
            }
            catch
            {
                // WMI недоступен/заблокирован политиками — просто пропускаем этот источник,
                // остальные всё равно дадут достаточно уникальности.
            }

            return "unknown";
        }
    }
}
