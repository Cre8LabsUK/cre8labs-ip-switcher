using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Cre8Labs.IPSwitcher.Core
{
    [DataContract]
    public sealed class Preset
    {
        [DataMember(Order = 1)] public string Name { get; set; } = "";
        [DataMember(Order = 2)] public string IP { get; set; } = "";
        [DataMember(Order = 3)] public string Mask { get; set; } = "255.255.255.0";
        [DataMember(Order = 4)] public string Gateway { get; set; } = "";
        [DataMember(Order = 5)] public string DNS1 { get; set; } = "";
        [DataMember(Order = 6)] public string DNS2 { get; set; } = "";

        public Preset Clone() => (Preset)MemberwiseClone();
    }

    [DataContract]
    public sealed class AppConfig
    {
        [DataMember(Order = 1)] public string Adapter { get; set; } = "";
        [DataMember(Order = 2)] public List<Preset> Presets { get; set; } = new List<Preset>();
    }

    /// <summary>
    /// Loads and saves presets.json. A presets.json next to the exe wins (portable mode),
    /// otherwise it lives in %APPDATA%\Cre8Labs\IP Switcher.
    /// </summary>
    public sealed class ConfigStore
    {
        static readonly DataContractJsonSerializer Serializer = new DataContractJsonSerializer(typeof(AppConfig));

        public string FilePath { get; }

        /// <summary>
        /// Set when presets.json exists but could not be read (locked, access denied).
        /// Saving is then blocked so the real presets are never overwritten with defaults.
        /// </summary>
        public bool ReadOnly { get; private set; }

        public ConfigStore()
        {
            var portable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "presets.json");
            FilePath = File.Exists(portable)
                ? portable
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cre8Labs", "IP Switcher", "presets.json");
        }

        public AppConfig Load()
        {
            if (NetworkService.DemoMode) { ReadOnly = false; return DemoData.Config(); }
            if (!File.Exists(FilePath)) return Defaults();

            string text = null;
            for (var attempt = 0; text == null; attempt++)
            {
                try { text = File.ReadAllText(FilePath); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // Another program (sync client, antivirus) may hold the file briefly.
                    if (attempt >= 4) { ReadOnly = true; return Defaults(); }
                    System.Threading.Thread.Sleep(150 * (attempt + 1));
                }
            }

            try
            {
                // Strip any BOM, then parse as UTF-8.
                text = text.TrimStart('﻿');
                using (var ms = new MemoryStream(new UTF8Encoding(false).GetBytes(text)))
                {
                    var config = (AppConfig)Serializer.ReadObject(ms);
                    config.Presets = config.Presets ?? new List<Preset>();
                    config.Presets.RemoveAll(p => p == null);
                    foreach (var p in config.Presets)
                    {
                        p.Name = p.Name ?? ""; p.IP = p.IP ?? ""; p.Mask = p.Mask ?? "";
                        p.Gateway = p.Gateway ?? ""; p.DNS1 = p.DNS1 ?? ""; p.DNS2 = p.DNS2 ?? "";
                    }
                    config.Adapter = config.Adapter ?? "";
                    return config;
                }
            }
            catch (Exception ex) when (ex is SerializationException || ex is ArgumentException || ex is InvalidCastException)
            {
                // The file is corrupt. Keep a copy so presets are never silently lost.
                try { File.Copy(FilePath, FilePath + ".bad", true); } catch (IOException) { }
                return Defaults();
            }
        }

        public void Save(AppConfig config)
        {
            if (NetworkService.DemoMode) return;
            if (ReadOnly) throw new IOException("presets.json could not be read at startup, so it was not overwritten. Restart the app to edit presets.");
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            var temp = FilePath + ".tmp";
            using (var fs = File.Create(temp))
            using (var writer = JsonReaderWriterFactory.CreateJsonWriter(fs, new UTF8Encoding(false), false, true, "  "))
            {
                Serializer.WriteObject(writer, config);
                writer.Flush();
                fs.Flush(true);
            }
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
            else File.Move(temp, FilePath);
        }

        static AppConfig Defaults() => new AppConfig
        {
            Presets = new List<Preset>
            {
                new Preset { Name = "LAN 192.168.1.x", IP = "192.168.1.50", Gateway = "192.168.1.1", DNS1 = "192.168.1.1", DNS2 = "8.8.8.8" },
                new Preset { Name = "LAN 192.168.0.x", IP = "192.168.0.50", Gateway = "192.168.0.1", DNS1 = "192.168.0.1", DNS2 = "8.8.8.8" },
                new Preset { Name = "LAN 10.0.0.x", IP = "10.0.0.50", Gateway = "10.0.0.1", DNS1 = "10.0.0.1", DNS2 = "8.8.8.8" },
                new Preset { Name = "Bench (no gateway)", IP = "192.168.1.250" },
            }
        };
    }
}
