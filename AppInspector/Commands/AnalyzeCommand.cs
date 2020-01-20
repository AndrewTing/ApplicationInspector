﻿// Copyright (C) Microsoft. All rights reserved.
// Licensed under the MIT License. See LICENSE.txt in the project root for license information.

using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
using ICSharpCode.SharpZipLib.Zip;
using MimeTypes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ICSharpCode.SharpZipLib.Core;
using RulesEngine;
using System.Text.RegularExpressions; 
using System.Text;
using Newtonsoft.Json;
using System.Threading;
using NLog;
using DotLiquid;

namespace Microsoft.AppInspector
{
    public class AnalyzeCommand : ICommand
    {
        readonly int MAX_FILESIZE = 1024 * 1000 * 5;  // Do not analyze files larger than 5 MB

        // Enable processing compressed files
        readonly string[] COMPRESSED_EXTENSIONS = "zip,gz,gzip,gem,tar,tgz,tar.gz,xz,7z".Split(",");
        readonly string[] EXCLUDEMATCH_FILEPATH = "sample,example,test,docs,.vs,.git".Split(",");

        Regex IgnoreMimeRegex;

        public enum ExitCode
        {
            NoMatches = 0,
            MatchesFound = 1,
            CriticalError = 2
        }

        IEnumerable<string> _srcfileList;
        AppProfile _appProfile;
        RuleProcessor _rulesProcessor;
        HashSet<string> _uniqueTagsControl;
        Writer _outputWriter;

        DateTime DateScanned { get; set; }
        DateTime _lastUpdated;

        /// <summary>
        /// Updated dynamically to more recent file in source
        /// </summary>
        public DateTime LastUpdated
        {
            get { return _lastUpdated;  }
            set
            {
                //find last updated file in solution
                if (_lastUpdated < value)
                    _lastUpdated = value;
            }
        }

        //cmdline arguments
        private string _arg_sourcePath;
        private string _arg_outputFile;
        private string _arg_fileFormat;
        private string _arg_outputTextFormat;
        private string _arg_customRulesPath;
        private bool _arg_ignoreDefaultRules;
        private bool _arg_outputUniqueTagsOnly;
        private string _arg_confidenceFilters;
        private bool _arg_simpleTagsOnly;
        private bool _arg_allowSampleFiles;
        private Confidence _arg_confidence;
        private WriteOnce.ConsoleVerbosity _arg_consoleVerbosityLevel;


        List<string> _fileExclusionList;//see exclusion list


        public AnalyzeCommand(AnalyzeCommandOptions opts)
        {
            _arg_sourcePath = opts.SourcePath;
            _arg_outputFile = opts.OutputFilePath;
            _arg_fileFormat = opts.OutputFileFormat;
            _arg_outputTextFormat = opts.TextOutputFormat;
            _arg_outputUniqueTagsOnly = !opts.AllowDupTags;
            _arg_allowSampleFiles = opts.AllowSampleFiles;
            _arg_customRulesPath = opts.CustomRulesPath;
            _arg_confidenceFilters = opts.ConfidenceFilters;
            _arg_ignoreDefaultRules = opts.IgnoreDefaultRules;
            _arg_simpleTagsOnly = opts.SimpleTagsOnly;
            if (!Enum.TryParse(opts.ConsoleVerbosityLevel, true, out _arg_consoleVerbosityLevel))
                throw new OpException(String.Format(ErrMsg.FormatString(ErrMsg.ID.CMD_INVALID_ARG_VALUE, "-x")));
            WriteOnce.Verbosity = _arg_consoleVerbosityLevel;

            IgnoreMimeRegex = new Regex(@"^(audio|video)/.*$");

            LastUpdated = DateTime.MinValue;
            DateScanned = DateTime.Now;

            ConfigOutput();
            ConfigSourcetoScan();
            ConfigConfidenceFilters();
            ConfigRules();

            _fileExclusionList = EXCLUDEMATCH_FILEPATH.ToList<string>();
            _uniqueTagsControl = new HashSet<string>();
        }


        #region configureMethods

        /// <summary>
        /// Expects user to supply all that apply
        /// </summary>
        void ConfigConfidenceFilters()
        {
            WriteOnce.SafeLog("AnalyzeCommand::ConfigConfidenceFilters", LogLevel.Trace);
            //parse and verify confidence values
            if (String.IsNullOrEmpty(_arg_confidenceFilters))
                _arg_confidence = Confidence.High | Confidence.Medium; //excludes low by default
            else
            {
                string[] confidences = _arg_confidenceFilters.Split(',');
                foreach (string confidence in confidences)
                {
                    Confidence single;
                    if (Enum.TryParse(confidence, true, out single))
                        _arg_confidence |= single;
                    else
                        throw new OpException(ErrMsg.FormatString(ErrMsg.ID.CMD_INVALID_ARG_VALUE, "x"));
                }
            }
        }




        /// <summary>
        /// Add default and/or custom rules paths
        /// Iterate paths and add to ruleset
        /// </summary>
        void ConfigRules()
        {
            WriteOnce.SafeLog("AnalyzeCommand::ConfigRules", LogLevel.Trace);

            RuleSet rulesSet = new RuleSet(Program.Logger);
            List<string> rulePaths = new List<string>();
            if (!_arg_ignoreDefaultRules)
                rulePaths.Add(Utils.GetPath(Utils.AppPath.defaultRules));

            if (!string.IsNullOrEmpty(_arg_customRulesPath))
                rulePaths.Add(_arg_customRulesPath);

            foreach (string rulePath in rulePaths)
            {
                if (Directory.Exists(rulePath))
                    rulesSet.AddDirectory(rulePath);
                else if (File.Exists(rulePath))
                    rulesSet.AddFile(rulePath);
                else
                {
                    throw new OpException(ErrMsg.FormatString(ErrMsg.ID.CMD_INVALID_RULE_PATH, rulePath));
                }
            }

            //error check based on ruleset not path enumeration
            if (rulesSet.Count() == 0)
            {
                throw new OpException(ErrMsg.GetString(ErrMsg.ID.CMD_NORULES_SPECIFIED));
            }

            //instantiate a RuleProcessor with the added rules and exception for dependency
            _rulesProcessor = new RuleProcessor(rulesSet, _arg_confidence, _arg_outputUniqueTagsOnly, _arg_simpleTagsOnly, Program.Logger);
            
            if (_arg_outputUniqueTagsOnly)
            {
                List<TagException> tagExceptions;
                if (File.Exists(Utils.GetPath(Utils.AppPath.tagCounterPref)))
                {
                    tagExceptions = JsonConvert.DeserializeObject<List<TagException>>(File.ReadAllText(Utils.GetPath(Utils.AppPath.tagCounterPref)));
                    string[] exceptions = new string[tagExceptions.Count];
                    for (int i = 0; i < tagExceptions.Count; i++)
                        exceptions[i] = tagExceptions[i].Tag;
                    _rulesProcessor.UniqueTagExceptions = exceptions;
                }
            }

            _appProfile = new AppProfile(_arg_sourcePath, rulePaths, false, _arg_simpleTagsOnly, _arg_outputUniqueTagsOnly);
            _appProfile.Args = "analyze -f " + _arg_fileFormat + " -u " + _arg_outputUniqueTagsOnly.ToString().ToLower() + " -v " +
                WriteOnce.Verbosity.ToString() + " -x " + _arg_confidence + " -i " + _arg_ignoreDefaultRules.ToString().ToLower();
    }

     
        void ConfigOutput()
        {
            WriteOnce.SafeLog("AnalyzeCommand::ConfigOutput", LogLevel.Trace);

            //Set output type, format and outstream
            _outputWriter = WriterFactory.GetWriter(_arg_fileFormat ?? "text", (string.IsNullOrEmpty(_arg_outputFile)) ? null : "text", _arg_outputTextFormat);
            if (!string.IsNullOrEmpty(_arg_outputFile))
            {
                 if (_arg_fileFormat != "html")
                    _outputWriter.TextWriter = File.CreateText(_arg_outputFile);//not needed if html output since application controlled

                _outputWriter.TextWriter.WriteLine(Program.GetVersionString());
            }
            else
                _outputWriter.TextWriter = Console.Out;

        }


        /// <summary>
        /// Simple validation on source path provided for scanning and preparation
        /// </summary>
        void ConfigSourcetoScan()
        {
            WriteOnce.SafeLog("AnalyzeCommand::ConfigSourcetoScan", LogLevel.Trace);

            if (Directory.Exists(_arg_sourcePath))
            {
                try
                {
                    _srcfileList = Directory.EnumerateFiles(_arg_sourcePath, "*.*", SearchOption.AllDirectories);
                    if (_srcfileList.Count() == 0)
                        throw new OpException(ErrMsg.FormatString(ErrMsg.ID.CMD_INVALID_FILE_OR_DIR, _arg_sourcePath));

                }
                catch (Exception)
                {
                    throw new OpException(ErrMsg.FormatString(ErrMsg.ID.CMD_INVALID_FILE_OR_DIR, _arg_sourcePath));
                }
            }
            else if (File.Exists(_arg_sourcePath)) //not a directory but make one for single flow
                _srcfileList = new List<string>() { _arg_sourcePath };
            else
                throw new OpException(ErrMsg.FormatString(ErrMsg.ID.CMD_INVALID_FILE_OR_DIR, _arg_sourcePath));

        }


        #endregion


        /// <summary>
        /// Main entry point to start analysis; handles setting up rules, directory enumeration
        /// file type detection and handoff
        /// Pre: All Configure Methods have been called already and we are ready to SCAN
        /// </summary>
        /// <returns></returns>
        public int Run()
        {
            WriteOnce.SafeLog("AnalyzeCommand::Run", LogLevel.Trace);

            DateTime start = DateTime.Now;          
            WriteOnce.Operation(ErrMsg.FormatString(ErrMsg.ID.CMD_RUNNING, "Analyze"));
       
            _appProfile.MetaData.TotalFiles = _srcfileList.Count();

            // Iterate through all files and process against rules
            foreach (string filename in _srcfileList)
            {
                var fileExtension = new FileInfo(filename).Extension;
                if (COMPRESSED_EXTENSIONS.Any(fileExtension.Contains))
                    UnZipAndProcess(filename); //determine if file is a compressed item to unpackage for processing
                else
                    ProcessAsFile(filename);
            }

            WriteOnce.General("\r"+ErrMsg.FormatString(ErrMsg.ID.ANALYZE_FILES_PROCESSED_PCNT, 100));
            WriteOnce.Operation(ErrMsg.GetString(ErrMsg.ID.CMD_PREPARING_REPORT));
           
            //Prepare report results
            _appProfile.MetaData.LastUpdated = LastUpdated.ToString();
            _appProfile.DateScanned = DateScanned.ToString();
            _appProfile.PrepareReport();
            TimeSpan timeSpan = start - DateTime.Now;
            WriteOnce.SafeLog(String.Format("Processing time: seconds:{0}", timeSpan.TotalSeconds * -1), LogLevel.Trace);
            FlushAll();

            //wrapup result status
            if (_appProfile.MetaData.TotalFiles == _appProfile.MetaData.FilesSkipped)
                WriteOnce.Error(ErrMsg.GetString(ErrMsg.ID.ANALYZE_NOSUPPORTED_FILETYPES));
            else if (_appProfile.MatchList.Count == 0)
                WriteOnce.Error(ErrMsg.GetString(ErrMsg.ID.ANALYZE_NOPATTERNS));
            else
                WriteOnce.Operation(ErrMsg.FormatString(ErrMsg.ID.CMD_COMPLETED, "Analyze"));
            
            return _appProfile.MatchList.Count() == 0 ? (int)ExitCode.NoMatches : 
                (int)ExitCode.MatchesFound;
        }



        /// <summary>
        /// Wrapper for files that are on disk and ready to read to allow separation of core
        /// scan evaluation for use by decompression methods as well
        /// </summary>
        /// <param name="filename"></param>
        void ProcessAsFile(string filename)
        {
            if (File.Exists(filename))
            {
                _appProfile.MetaData.FileNames.Add(filename);
                _appProfile.MetaData.PackageTypes.Add(ErrMsg.GetString(ErrMsg.ID.ANALYZE_UNCOMPRESSED_FILETYPE));

                string fileText = File.ReadAllText(filename);
                ProcessInMemory(filename, fileText);
            }
            else
            {
                throw new OpException(ErrMsg.FormatString(ErrMsg.ID.CMD_INVALID_FILE_OR_DIR, filename));
            }
        }


        /// <summary>
        /// Main WORKHORSE for analyzing file; called from file based or decompression functions
        /// </summary>
        /// <param name="filename"></param>
        /// <param name="fileText"></param>
        void ProcessInMemory(string filePath, string fileText)
        {
            #region quickvalidation
            if (fileText.Length > MAX_FILESIZE)
            {
                WriteOnce.SafeLog(ErrMsg.FormatString(ErrMsg.ID.ANALYZE_FILESIZE_SKIPPED, filePath), LogLevel.Warn);
                _appProfile.MetaData.FilesSkipped++;
                return;
            }

            //exclude sample, test or similar files by default or as specified in exclusion list
            if (!_arg_allowSampleFiles && _fileExclusionList.Any(v => filePath.ToLower().Contains(v)))
            {
                WriteOnce.SafeLog("Part of excluded list: " + filePath, LogLevel.Trace);
                WriteOnce.SafeLog(ErrMsg.FormatString(ErrMsg.ID.ANALYZE_FILESIZE_SKIPPED, filePath), LogLevel.Trace);
                _appProfile.MetaData.FilesSkipped++;
                return;
            }

            //check for supported language
            LanguageInfo languageInfo = new LanguageInfo();
            // Skip files written in unknown language
            if (!Language.FromFileName(filePath, ref languageInfo))
            {
                WriteOnce.SafeLog("Language not found for file: " + filePath, LogLevel.Trace);
                _appProfile.MetaData.FilesSkipped++;
                return;
            }
            else
            {
                WriteOnce.SafeLog("Preparing to process file: " + filePath, LogLevel.Trace);
            }

            #endregion

            #region minorRollupTrackingAndProgress

            _appProfile.MetaData.FilesAnalyzed++;
            _appProfile.MetaData.AddLanguage(languageInfo.Name);
            _appProfile.MetaData.FileExtensions.Add(Path.GetExtension(filePath).Replace('.', ' ').TrimStart());
            LastUpdated = File.GetLastWriteTime(filePath);

            int totalFilesReviewed = _appProfile.MetaData.FilesAnalyzed + _appProfile.MetaData.FilesSkipped;
            int percentCompleted = (int)((float)totalFilesReviewed / (float)_appProfile.MetaData.TotalFiles * 100);
            WriteOnce.General("\r" + ErrMsg.FormatString(ErrMsg.ID.ANALYZE_FILES_PROCESSED_PCNT, percentCompleted), false);

            #endregion

            //process file against rules
            Issue[] matches = _rulesProcessor.Analyze(fileText, languageInfo.Name);

            //if any matches found for this file...
            if (matches.Count() > 0)
            {              
                _appProfile.MetaData.FilesAffected++;
                _appProfile.MetaData.TotalMatchesCount += matches.Count();

                // Iterate through each match issue 
                foreach (Issue match in matches)
                {
                    WriteOnce.SafeLog(string.Format("Processing pattern matches for ruleId {0}, ruleName {1} file {2}", match.Rule.Id, match.Rule.Name, filePath), LogLevel.Trace);

                    //do not accept features from build type files (only metadata) to avoid false positives that are not part of the executable program
                    if (languageInfo.Type == LanguageInfo.LangFileType.Build && match.Rule.Tags.Any(v => !v.Contains("Metadata")))
                        continue;

                    //maintain a list of unique tags; multi-purpose but primarily for filtering -d option
                    bool dupTagFound = false;
                    foreach (string t in match.Rule.Tags)
                        dupTagFound = !_uniqueTagsControl.Add(t);

                    //save all unique dependendencies even if Dependency tag pattern is not-unique
                    var tagPatternRegex = new Regex("Dependency.SourceInclude", RegexOptions.IgnoreCase);
                    String textMatch;
                    if (match.Rule.Tags.Any(v => tagPatternRegex.IsMatch(v)))
                        textMatch = ExtractDependency(fileText, match.Boundary.Index, match.PatternMatch, languageInfo.Name);
                    else
                        textMatch = ExtractTextSample(fileText, match.Boundary.Index, match.Boundary.Length);

                    //wrap rule issue result to add metadata
                    MatchRecord record = new MatchRecord()
                    {
                        Filename = filePath,
                        Language = languageInfo,
                        Filesize = fileText.Length,
                        TextSample = textMatch,
                        Excerpt = ExtractExcerpt(fileText, match.StartLocation.Line),
                        Issue = match
                    };

                    //preserve issue level characteristics as rolled up meta data of interest
                    bool addAsFeatureMatch = _appProfile.MetaData.AddStandardProperties(record);

                    //bail after extracting any dependency unique items IF user requested
                    if (_arg_outputUniqueTagsOnly && dupTagFound)
                        continue;
                    else if (addAsFeatureMatch)
                        _appProfile.MatchList.Add(record);
                }
            }
            else
            {
                WriteOnce.SafeLog("No pattern matches detected for file: " + filePath, LogLevel.Trace);
            }

        }

        #region PostRulesMatchProcessingAssist

        /// <summary>
        /// Simple but keeps calling code consistent
        /// </summary>
        /// <param name="fileText"></param>
        /// <param name="index"></param>
        /// <param name="length"></param>
        /// <returns></returns>
        string ExtractTextSample(string fileText, int index, int length)
        {
            string result = "";
            try
            {
                result = fileText.Substring(index, length).Trim();
            }
            catch (Exception)
            {
                //control the error description and continue; error in rules engine possible
                WriteOnce.SafeLog("Unexpected indexing issue in ExtractTextSample.  Process continued", LogLevel.Error);
            }

            return result;
        }

        /// <summary>
        /// Helper to special case additional processing to just get the values without the import keywords etc.
        /// and encode for html output
        /// </summary>
        /// <param name="text"></param>
        /// <param name="language"></param>
        /// <returns></returns>
        string ExtractDependency(string text, int startIndex, SearchPattern pattern, string language)
        {
            // import value; load value; include value; 
            string rawResult = "";
            int endIndex = text.IndexOf('\n', startIndex);
            if (-1 != startIndex && -1 != endIndex)
            {
                rawResult = text.Substring(startIndex, endIndex - startIndex).Trim();

                //recreate regex used to find entire value
                Regex regex = new Regex(pattern.Pattern);
                MatchCollection matches = regex.Matches(rawResult);
                
                //remove surrounding import or trailing comments 
                if (matches.Count > 0)
                { 
                    foreach (Match match in matches)
                    {
                        if (match.Groups.Count == 1)//handles cases like "using Newtonsoft.Json"
                        {
                            string[] parseValues = match.Groups[0].Value.Split(' ');
                            if (parseValues.Length == 1)
                                rawResult = parseValues[0].Trim();
                            else if (parseValues.Length > 1)
                                rawResult = parseValues[1].Trim(); //should be value; time will tell if fullproof
                        }
                        else if (match.Groups.Count > 1)//handles cases like include <stdio.h>
                            rawResult = match.Groups[1].Value.Trim();
                        //else if > 2 too hard to match; do nothing

                        break;//only designed to expect one match per line i.e. not include value include value
                    }
                }

                String finalResult = rawResult.Replace(";", "");
                _appProfile.MetaData.UniqueDependencies.Add(finalResult);

                return System.Net.WebUtility.HtmlEncode(finalResult);
            }

            return rawResult;
        }
        
        /// <summary>
        /// Located here to include during Match creation to avoid a call later or putting in constructor
        /// Needed in match ensuring value exists at time of report writing rather than expecting a callback
        /// from the template
        /// </summary>
        /// <param name="fileName"></param>
        /// <param name="startLineNumber"></param>
        /// <param name="length"></param>
        /// <returns></returns>
        private string ExtractExcerpt(string text, int startLineNumber, int length = 10)
        {
            if (String.IsNullOrEmpty(text))
            {
                return "";
            }

            var lines = text.Split('\n');
            var distance = (int)((length - 1.0) / 2.0);

            // Sanity check
            if (startLineNumber < 0) startLineNumber = 0;
            if (startLineNumber >= lines.Length) startLineNumber = lines.Length - 1;

            var excerptStartLine = Math.Max(0, startLineNumber - distance);
            var excerptEndLine = Math.Min(lines.Length - 1, startLineNumber + distance);
            
            /* This is a little wacky, but if the code snippet we're viewing is already
             * indented 16 characters minimum, we don't want to show all that extra white-
             * space, so we'll find the smallest number of spaces at the beginning of
             * each line and use that.
             */
            var n = (int)Math.Floor(Math.Log10(excerptEndLine) + 1);
            var minSpaces = -1;
            for (var i = excerptStartLine; i <= excerptEndLine; i++)
            {
                var numPrefixSpaces = lines[i].TakeWhile(c => c == ' ').Count();
                minSpaces = (minSpaces == -1 || numPrefixSpaces < minSpaces) ? numPrefixSpaces : minSpaces;
            }

            var sb = new StringBuilder();
            // We want to go from (start - 5) to (start + 5) (off by one?)
            // LINE=10, len=5, we want 8..12, so N-(L-1)/2 to N+(L-1)/2
            // But cap those values at 0/end
            for (var i = excerptStartLine; i <= excerptEndLine; i++)
            {
                string line = lines[i].Substring(minSpaces).TrimEnd();
                sb.AppendLine(line);
                //string line = System.Net.WebUtility.HtmlEncode(lines[i].Substring(minSpaces).TrimEnd());
                //sb.AppendFormat("{0}  {1}\n", (i + 1).ToString().PadLeft(n, ' '), line);
            }

            return System.Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()));
        }

    

        public void FlushAll()
        {
            if (_outputWriter != null)
            {
                _outputWriter.WriteApp(_appProfile);

                if (_outputWriter.TextWriter != null && _arg_fileFormat != "html")
                {
                    _outputWriter.FlushAndClose();//not required for html formal i.e. multiple files already closed
                    _outputWriter = null;
                    if (!String.IsNullOrEmpty(_arg_outputFile))
                        WriteOnce.Any(ErrMsg.FormatString(ErrMsg.ID.ANALYZE_OUTPUT_FILE, _arg_outputFile));
                    else
                        WriteOnce.NewLine();
                }
            }
        }

        #endregion


        #region ZipDecompression

        void UnZipAndProcess(string filename)
        {
            if (!File.Exists(filename))
            {
                throw new OpException(ErrMsg.FormatString(ErrMsg.ID.CMD_INVALID_FILE_OR_DIR, filename));
            }

            //zip itself may be too huge for timely processing
            if (new FileInfo(filename).Length > MAX_FILESIZE)
            {
                throw new OpException(ErrMsg.FormatString(ErrMsg.ID.ANALYZE_FILESIZE_HALT, filename));
            }

            // Ignore images and other junk like that
            var fileExtension = new FileInfo(filename).Extension;
            var mimeType = MimeTypeMap.GetMimeType(fileExtension);
            bool mimeMatch = false;
            if (!IgnoreMimeRegex.IsMatch(mimeType))
            {
                var isValidExtension = COMPRESSED_EXTENSIONS.Any(fileExtension.Contains);
                if (isValidExtension || fileExtension == "ts")
                    mimeMatch = true;
                else if (mimeType.Contains("zip", StringComparison.CurrentCultureIgnoreCase) || // Should have been caught in file extensions above, but still OK.
                    mimeType.Contains("tar", StringComparison.CurrentCultureIgnoreCase) ||
                    mimeType.Contains("compressed", StringComparison.CurrentCultureIgnoreCase))
                    mimeMatch = true;

                if (mimeMatch)
                {
                    // Now process the file
                    switch (fileExtension)
                    {
                        case ".tgz":
                            ProcessTarGzFile(filename);
                            break;
                        case ".gz":
                            if (filename.Contains(".tar.gz"))
                            {
                                ProcessTarGzFile(filename);
                            }
                            else
                            {
                                WriteOnce.SafeLog("no support for .gz unless .tar.gz: " + fileExtension, LogLevel.Warn);
                                _appProfile.MetaData.PackageTypes.Add("compressed-unsupported");
                            }
                            break;
                        case ".jar":
                        case ".zip":
                            ProcessZipFile(filename);
                            break;
                        case ".gem":
                        case ".tar":
                        case ".nupkg":
                            WriteOnce.SafeLog($"Processing of {fileExtension} not implemented yet.", LogLevel.Warn);
                            break;
                        default:
                            WriteOnce.SafeLog("no support for compressed type: " + fileExtension, LogLevel.Warn);
                            break;
                    }

                    _appProfile.MetaData.PackageTypes.Add("compressed");

                }
                else
                    _appProfile.MetaData.PackageTypes.Add("compressed-unsupported");


            }
        }

        void ProcessZipFile(string filename)
        {
            WriteOnce.SafeLog(string.Format("Analyzing .zip file: [{0}])", filename), LogLevel.Trace);

            ZipFile zipFile;
            using (var fileStream = new FileStream(filename, FileMode.Open, FileAccess.Read))
            using (var memoryStream = new MemoryStream())
            {
                zipFile = new ZipFile(fileStream);
                foreach (ZipEntry zipEntry in zipFile)
                {
                    if (zipEntry.IsDirectory)
                    {
                        continue;
                    }
                    byte[] buffer = new byte[4096];
                    var zipStream = zipFile.GetInputStream(zipEntry);
                    if (zipEntry.Size > MAX_FILESIZE)
                    {
                        _appProfile.MetaData.FilesSkipped++;
                        WriteOnce.SafeLog(ErrMsg.FormatString(ErrMsg.ID.ANALYZE_FILESIZE_SKIPPED, zipEntry.Name), LogLevel.Warn);
                        zipFile.Close();
                        continue;
                    }

                    StreamUtils.Copy(zipStream, memoryStream, buffer);
                    var mimeType = MimeTypeMap.GetMimeType(Path.GetExtension(zipEntry.Name));
                    if (IgnoreMimeRegex.IsMatch(mimeType) && new FileInfo(zipEntry.Name).Extension != ".ts")
                    {
                        _appProfile.MetaData.FilesSkipped++;
                        WriteOnce.SafeLog(string.Format("Ignoring zip entry [{0}]", zipEntry.Name), LogLevel.Error);
                    }
                    else
                    {
                        byte[] streamByteArray = memoryStream.ToArray();
                        ProcessInMemory(Path.GetFileName(zipEntry.Name), Encoding.UTF8.GetString(streamByteArray, 0, streamByteArray.Length));
                    }
                    memoryStream.SetLength(0);   // Clear out the stream
                }

                zipFile.Close();
            }

        }


        /// <summary>
        /// Helper if no other way to get file count for progress meter
        /// </summary>
        /// <param name="filename"></param>
        /// <returns></returns>
        int GetTarGzFileCount(string filename)
        {
            int count = 0;
            using (var fileStream = new FileStream(filename, FileMode.Open, FileAccess.Read))
            using (var gzipStream = new GZipInputStream(fileStream))
            using (var memoryStream = new MemoryStream())
            {
                var tarStream = new TarInputStream(gzipStream);
                TarEntry tarEntry;
                while ((tarEntry = tarStream.GetNextEntry()) != null)
                {
                    if (tarEntry.IsDirectory)
                    {
                        continue;
                    }

                    count++;

                }
            }

            Thread.Sleep(300);//give time for OS to close before full processing
            return count;
        }


        void ProcessTarGzFile(string filename)
        {
            WriteOnce.SafeLog(string.Format("Analyzing .tar.gz file: [{0}]", filename), LogLevel.Trace);

            using (var fileStream = new FileStream(filename, FileMode.Open, FileAccess.Read))
            using (var gzipStream = new GZipInputStream(fileStream))
            using (var memoryStream = new MemoryStream())
            {
                var tarStream = new TarInputStream(gzipStream);
                TarEntry tarEntry;
                while ((tarEntry = tarStream.GetNextEntry()) != null)
                {
                    if (tarEntry.IsDirectory)
                    {
                        continue;
                    }
                    tarStream.CopyEntryContents(memoryStream);
                    if (tarEntry.Size > MAX_FILESIZE)
                    {
                        _appProfile.MetaData.FilesSkipped++;
                        WriteOnce.SafeLog(ErrMsg.FormatString(ErrMsg.ID.ANALYZE_FILESIZE_SKIPPED, tarEntry.Name), LogLevel.Warn);
                        tarStream.Close();
                        continue;
                    }

                    var mimeType = MimeTypeMap.GetMimeType(Path.GetExtension(tarEntry.Name));
                    if (IgnoreMimeRegex.IsMatch(mimeType) && new FileInfo(tarEntry.Name).Extension != ".ts")
                    {
                        _appProfile.MetaData.FilesSkipped++;
                        WriteOnce.SafeLog(string.Format("Ignoring tar entry [{0}]", tarEntry.Name), LogLevel.Error);
                    }
                    else
                    {
                        //file name may contain slashes; remove prior to call
                        byte[] streamByteArray = memoryStream.ToArray();
                        ProcessInMemory(Path.GetFileName(tarEntry.Name), Encoding.UTF8.GetString(streamByteArray, 0, streamByteArray.Length));
                    }

                    memoryStream.SetLength(0);   // Clear out the stream
                }

                tarStream.Close();
            }

        }

        #endregion


    }
  
}



