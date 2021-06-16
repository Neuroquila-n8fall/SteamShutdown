﻿using Microsoft.Win32;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace SteamShutdown
{
    public static partial class Steam
    {
        static readonly Regex singleLine = new Regex("^(\\t+\".+\")\\t\\t(\".*\")$", RegexOptions.Compiled);
        static readonly Regex startOfObject = new Regex("^\\t+\".+\"$", RegexOptions.Compiled);

        public static List<App> Apps { get; private set; } = new List<App>();

        const string STEAM_REG_VALUE = "InstallPath";

        static Steam()
        {
            string steamRegistryPath = GetSteamRegistryPath();
            var rg = Registry.LocalMachine.OpenSubKey(steamRegistryPath, true);
            string installationPath = rg.GetValue(STEAM_REG_VALUE, null) as string;
            if (installationPath == null)
            {
                MessageBox.Show("Steam is not installed.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.Exit(0);
            }

            if (!Directory.Exists(installationPath))
            {
                var key = Path.Combine(steamRegistryPath, STEAM_REG_VALUE);

                DialogResult mb = MessageBox.Show("Seems a registry value is wrong, probably because of moving Steam to another location." + Environment.NewLine
                    + $"I can try to fix that for you. For that I will delete this registry value: {key}" + Environment.NewLine
                    + "You have to restart Steam afterwards since this will set the correct value for this registry value." + Environment.NewLine
                    + Environment.NewLine
                    + "If you click \"Yes\", the registry value will be deleted and SteamShutdown closed. Then first restart Steam before opening SteamShutdown again." + Environment.NewLine
                    + "If you click \"No\", you can select the installation path by yourself.",
                    "Error",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (mb == DialogResult.Yes)
                {
                    rg.DeleteValue("InstallPath");
                    Environment.Exit(0);
                }

                FolderBrowserDialog fbd = new FolderBrowserDialog();
                fbd.Description = "Your steam folder could not be automatically detected."
                    + Environment.NewLine
                    + "Please select the root of your steam folder."
                    + Environment.NewLine
                    + "Example: " + @"C:\Program Files (x86)\Steam";
                DialogResult re = fbd.ShowDialog();
                if (re != DialogResult.OK) return;
                installationPath = fbd.SelectedPath;
            }

            rg.Close();

            string[] libraryPaths = GetLibraryPaths(installationPath);
            if (libraryPaths.Length == 0)
            {
                MessageBox.Show("No game library found." + Environment.NewLine + "This might appear if Steam has been installed on this machine but was uninstalled.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.Exit(0);
            }

            UpdateAppInfos(libraryPaths);

            foreach (string libraryFolder in libraryPaths)
            {
                var fsw = new FileSystemWatcher(libraryFolder, "*.acf");
                fsw.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName;
                fsw.Changed += Fsw_Changed;
                fsw.Deleted += Fsw_Deleted;
                fsw.EnableRaisingEvents = true;
            }
        }


        public static int IdFromAcfFilename(string filename)
        {
            string filenameWithoutExtension = Path.GetFileNameWithoutExtension(filename);

            int loc = filenameWithoutExtension.IndexOf('_');
            return int.Parse(filenameWithoutExtension.Substring(loc + 1));
        }

        private static void UpdateAppInfos(IEnumerable<string> libraryPaths)
        {
            var appInfos = new List<App>();

            foreach (string path in libraryPaths)
            {
                DirectoryInfo di = new DirectoryInfo(path);

                foreach (FileInfo fileInfo in di.EnumerateFiles("*.acf"))
                {
                    // Skip if file is empty
                    if (fileInfo.Length == 0) continue;

                    App ai = FileToAppInfo(fileInfo.FullName);
                    if (ai == null) continue;

                    appInfos.Add(ai);
                }
            }


            Apps = appInfos.OrderBy(x => x.Name).ToList();
        }

        public static App FileToAppInfo(string filename)
        {
            string[] content = File.ReadAllLines(filename);

            // Skip if file contains only NULL bytes (this can happen sometimes, example: download crashes, resulting in a corrupted file)
            if (content.Length == 1 && string.IsNullOrWhiteSpace(content[0].TrimStart('\0'))) return null;

            string json = AcfToJson(content);
            dynamic stuff = JsonConvert.DeserializeObject(json);

            if (stuff == null)
            {
                MessageBox.Show(
                    $"{filename}{Environment.NewLine}contains unexpected content.{Environment.NewLine}This game will be ignored.",
                    "Warning",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }

            App ai = JsonToAppInfo(stuff);
            return ai;
        }

        private static App JsonToAppInfo(dynamic json)
        {
            App newInfo = new App
            {
                ID = int.Parse((json.appid ?? json.appID ?? json.AppID).ToString()),
                Name = json.name ?? json.installdir,
                State = int.Parse(json.StateFlags.ToString())
            };

            return newInfo;
        }

        private static string AcfToJson(string[] acfLines)
        {
            StringBuilder sb = new StringBuilder(acfLines.Length - 1);

            for (int i = 1; i < acfLines.Length; i++)
            {
                Match mSingle = singleLine.Match(acfLines[i]);
                if (mSingle.Success)
                {
                    sb.Append(mSingle.Groups[1].Value);
                    sb.Append(": ");
                    sb.Append(mSingle.Groups[2].Value);

                    // Last value of object must not have a tailing comma
                    if (i + 1 < acfLines.Length && acfLines[i + 1].EndsWith("}"))
                        sb.AppendLine();
                    else
                        sb.AppendLine(",");
                }
                else if (acfLines[i].StartsWith("\t") && acfLines[i].EndsWith("}"))
                {
                    sb.Append(acfLines[i]);

                    if (i + 1 < acfLines.Length && acfLines[i + 1].EndsWith("}"))
                        sb.AppendLine();
                    else
                        sb.AppendLine(",");
                }
                else if (startOfObject.IsMatch(acfLines[i]))
                {
                    sb.Append(acfLines[i]);
                    sb.AppendLine(":");
                }
                else
                {
                    sb.AppendLine(acfLines[i]);
                }
            }

            return sb.ToString();
        }

        private static string[] GetLibraryPaths(string installationPath)
        {
            List<string> paths = new List<string>()
                {
                    Path.Combine(installationPath, "SteamApps")
                };

            string libraryFoldersPath = Path.Combine(installationPath, "SteamApps", "libraryfolders.vdf");

            //Issue #34 and related: Crash on Startup because the "path" isn't parsed but instead the whole block is considered. This is due to the fact that it appears Valve has changed the layout of this file or it has something to do with multiple library folders defined.
            //                       Either way it is more secure to search through the file and extract any "path" occurrences rather than trying to deserialize the whole file.

            //First we will read the whole library config file into memory
            var vdfRaw = File.ReadAllLines(libraryFoldersPath);
            //Cycle through each line of text
            //This method is rather slow but speed shouldn't be a concern since this file is parsed once the app starts and even on slow disks this should perform quick enough.
            foreach (var s in vdfRaw)
            {
                //Check if a line contains the "path" token
                if (!s.ToLower().Contains("path"))
                {
                    continue;
                }
                //We should have a line like this:
                //          "path"		"E:\\SteamLibrary"
                //Get rid of any trailing white-spaces and any quote marks
                var sanitizedLine = s.Trim().Replace(@"""", string.Empty);
                //It looks like this now:
                //path	\t\t	E:\\SteamLibrary
                //Check again if the layout of the string matches expectations - in this case the resulting string should start with "path" and nothing else.
                if (sanitizedLine.ToLower().StartsWith("path"))
                {
                    //We know that each key-value pair is separated by \t
                    //Splitting it by \t while omitting empty results will leave us with an array of two values. 
                    var pathKeyValuePair = sanitizedLine.Split(new[]{
                        '\t'
                    }, StringSplitOptions.RemoveEmptyEntries);

                    //Check if we got less than 2 entries.
                    if (pathKeyValuePair.Length < 2)
                    {
                        continue;
                    }

                    //Append SteamApps folder
                    var steamAppsPath = Path.Combine(pathKeyValuePair[1], "SteamApps");
                    //Check if it exists, bail if not.
                    if (!Directory.Exists(steamAppsPath))
                    {
                        continue;
                    }
                    //Path exists, ready to add to collection.
                    paths.Add(steamAppsPath);
                }
            }

            return paths.ToArray();
        }

        private static string GetSteamRegistryPath()
        {
            string start = @"SOFTWARE\";
            if (Environment.Is64BitOperatingSystem)
            {
                start += @"Wow6432Node\";
            }

            return start + @"Valve\Steam";
        }
    }
}
