using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Rage;

namespace BackupPlacement
{
    public static class BetterBackupConfig
    {
        private const string IniFileName =
            "BetterBackup.ini";

        public static Keys PlacementConfirmKey
        {
            get;
            private set;
        } = Keys.H;

        public static Keys ForceTeleportKey
        {
            get;
            private set;
        } = Keys.H;

        public static Keys PlacementCancelKey
        {
            get;
            private set;
        } = Keys.E;

        public static Keys RotateLeftKey
        {
            get;
            private set;
        } = Keys.NumPad4;

        public static Keys RotateRightKey
        {
            get;
            private set;
        } = Keys.NumPad6;

        public static Keys ParkBehindCruiserKey
        {
            get;
            private set;
        } = Keys.NumPad5;

        public static string IniPath
        {
            get;
            private set;
        }

        public static void Load()
        {
            ResetDefaults();

            IniPath =
                ResolveIniPath();

            try
            {
                if (!File.Exists(IniPath))
                {
                    WriteDefaultIni();

                    Game.LogTrivial(
                        "[BetterBackup] Config file did not exist. " +
                        $"Created default INI at: {IniPath}"
                    );

                    return;
                }

                Dictionary<string, string> values =
                    ReadKeysSection(IniPath);

                PlacementConfirmKey =
                    ReadKey(
                        values,
                        "PlacementConfirmKey",
                        Keys.H
                    );

                ForceTeleportKey =
                    ReadKey(
                        values,
                        "ForceTeleportKey",
                        Keys.H
                    );

                PlacementCancelKey =
                    ReadKey(
                        values,
                        "PlacementCancelKey",
                        Keys.E
                    );

                RotateLeftKey =
                    ReadKey(
                        values,
                        "RotateLeftKey",
                        Keys.NumPad4
                    );

                RotateRightKey =
                    ReadKey(
                        values,
                        "RotateRightKey",
                        Keys.NumPad6
                    );

                ParkBehindCruiserKey =
                    ReadKey(
                        values,
                        "ParkBehindCruiserKey",
                        Keys.NumPad5
                    );

                Game.LogTrivial(
                    "[BetterBackup] Config loaded. " +
                    $"Confirm={PlacementConfirmKey}, " +
                    $"ForceTeleport={ForceTeleportKey}, " +
                    $"Cancel={PlacementCancelKey}, " +
                    $"RotateLeft={RotateLeftKey}, " +
                    $"RotateRight={RotateRightKey}, " +
                    $"ParkBehindCruiser={ParkBehindCruiserKey}"
                );
            }
            catch (Exception ex)
            {
                ResetDefaults();

                Game.LogTrivial(
                    "[BetterBackup] Config load ERROR. " +
                    "Using default key bindings. " +
                    ex
                );
            }
        }

        public static string GetDisplayName(
            Keys key
        )
        {
            switch (key)
            {
                case Keys.NumPad0:
                    return "NUM 0";

                case Keys.NumPad1:
                    return "NUM 1";

                case Keys.NumPad2:
                    return "NUM 2";

                case Keys.NumPad3:
                    return "NUM 3";

                case Keys.NumPad4:
                    return "NUM 4";

                case Keys.NumPad5:
                    return "NUM 5";

                case Keys.NumPad6:
                    return "NUM 6";

                case Keys.NumPad7:
                    return "NUM 7";

                case Keys.NumPad8:
                    return "NUM 8";

                case Keys.NumPad9:
                    return "NUM 9";

                case Keys.Return:
                    return "ENTER";

                case Keys.Escape:
                    return "ESC";

                case Keys.Back:
                    return "BACKSPACE";

                case Keys.Space:
                    return "SPACE";

                case Keys.LShiftKey:
                    return "LEFT SHIFT";

                case Keys.RShiftKey:
                    return "RIGHT SHIFT";

                case Keys.LControlKey:
                    return "LEFT CTRL";

                case Keys.RControlKey:
                    return "RIGHT CTRL";

                case Keys.LMenu:
                    return "LEFT ALT";

                case Keys.RMenu:
                    return "RIGHT ALT";

                default:
                    return key.ToString()
                        .ToUpperInvariant();
            }
        }

        private static void ResetDefaults()
        {
            PlacementConfirmKey =
                Keys.H;

            ForceTeleportKey =
                Keys.H;

            PlacementCancelKey =
                Keys.E;

            RotateLeftKey =
                Keys.NumPad4;

            RotateRightKey =
                Keys.NumPad6;

            ParkBehindCruiserKey =
                Keys.NumPad5;
        }

        private static string ResolveIniPath()
        {
            try
            {
                string assemblyLocation =
                    Assembly.GetExecutingAssembly()
                        .Location;

                if (!string.IsNullOrWhiteSpace(
                    assemblyLocation))
                {
                    string directory =
                        Path.GetDirectoryName(
                            assemblyLocation
                        );

                    if (!string.IsNullOrWhiteSpace(
                        directory))
                    {
                        return Path.Combine(
                            directory,
                            IniFileName
                        );
                    }
                }
            }
            catch
            {
            }

            return Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Plugins",
                "LSPDFR",
                IniFileName
            );
        }

        private static Dictionary<string, string> ReadKeysSection(
            string path
        )
        {
            Dictionary<string, string> values =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase
                );

            bool inKeysSection =
                false;

            foreach (string rawLine
                in File.ReadAllLines(path))
            {
                string line =
                    rawLine.Trim();

                if (line.Length == 0 ||
                    line.StartsWith(";") ||
                    line.StartsWith("#"))
                {
                    continue;
                }

                if (line.StartsWith("[") &&
                    line.EndsWith("]"))
                {
                    string section =
                        line.Substring(
                            1,
                            line.Length - 2
                        ).Trim();

                    inKeysSection =
                        string.Equals(
                            section,
                            "Keys",
                            StringComparison.OrdinalIgnoreCase
                        );

                    continue;
                }

                if (!inKeysSection)
                {
                    continue;
                }

                int equalsIndex =
                    line.IndexOf('=');

                if (equalsIndex <= 0)
                {
                    continue;
                }

                string key =
                    line.Substring(
                        0,
                        equalsIndex
                    ).Trim();

                string value =
                    line.Substring(
                        equalsIndex + 1
                    ).Trim();

                int semicolonComment =
                    value.IndexOf(';');

                if (semicolonComment >= 0)
                {
                    value =
                        value.Substring(
                            0,
                            semicolonComment
                        ).Trim();
                }

                int hashComment =
                    value.IndexOf('#');

                if (hashComment >= 0)
                {
                    value =
                        value.Substring(
                            0,
                            hashComment
                        ).Trim();
                }

                if (key.Length == 0)
                {
                    continue;
                }

                values[key] =
                    value;
            }

            return values;
        }

        private static Keys ReadKey(
            Dictionary<string, string> values,
            string settingName,
            Keys defaultValue
        )
        {
            string rawValue;

            if (!values.TryGetValue(
                settingName,
                out rawValue) ||
                string.IsNullOrWhiteSpace(rawValue))
            {
                return defaultValue;
            }

            string normalized =
                NormalizeKeyName(rawValue);

            Keys parsed;

            if (Enum.TryParse(
                normalized,
                true,
                out parsed))
            {
                return parsed;
            }

            Game.LogTrivial(
                "[BetterBackup] Invalid key value " +
                $"'{rawValue}' for {settingName}. " +
                $"Using default {defaultValue}."
            );

            return defaultValue;
        }

        private static string NormalizeKeyName(
            string value
        )
        {
            string trimmed =
                value.Trim();

            if (trimmed.Equals(
                "Enter",
                StringComparison.OrdinalIgnoreCase))
            {
                return "Return";
            }

            if (trimmed.Equals(
                "Esc",
                StringComparison.OrdinalIgnoreCase))
            {
                return "Escape";
            }

            if (trimmed.StartsWith(
                "Numpad",
                StringComparison.OrdinalIgnoreCase))
            {
                return "NumPad" +
                    trimmed.Substring(6);
            }

            if (trimmed.StartsWith(
                "Num",
                StringComparison.OrdinalIgnoreCase) &&
                trimmed.Length == 4 &&
                char.IsDigit(trimmed[3]))
            {
                return "NumPad" +
                    trimmed.Substring(3);
            }

            return trimmed;
        }

        private static void WriteDefaultIni()
        {
            string directory =
                Path.GetDirectoryName(
                    IniPath
                );

            if (!string.IsNullOrWhiteSpace(
                directory))
            {
                Directory.CreateDirectory(
                    directory
                );
            }

            File.WriteAllText(
                IniPath,
                DefaultIniText
            );
        }

        private const string DefaultIniText =
@"; BetterBackup Configuration

[Keys]

PlacementConfirmKey=H
ForceTeleportKey=H
PlacementCancelKey=E
RotateLeftKey=NumPad4
ParkBehindCruiserKey=NumPad5
RotateRightKey=NumPad6
";
    }
}