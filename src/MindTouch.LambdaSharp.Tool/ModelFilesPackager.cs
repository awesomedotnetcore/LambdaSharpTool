/*
 * MindTouch λ#
 * Copyright (C) 2018 MindTouch, Inc.
 * www.mindtouch.com  oss@mindtouch.com
 *
 * For community documentation and downloads visit mindtouch.com;
 * please review the licensing section.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using MindTouch.LambdaSharp.Tool.Internal;
using MindTouch.LambdaSharp.Tool.Model;

namespace MindTouch.LambdaSharp.Tool {

    public class ModelFilesPackager : AModelProcessor {

        //--- Fields ---
        private Module _module;

        //--- Constructors ---
        public ModelFilesPackager(Settings settings, string sourceFilename) : base(settings, sourceFilename) { }

        //--- Methods ---
        public void Process(Module module) {
            _module = module;
<<<<<<< HEAD
            foreach(var entry in module.Entries.OfType<PackageEntry>()) {
                AtLocation(entry.FullName, () => {
                    ProcessParameter(entry);
=======
            foreach(var parameter in module.Variables.Where(p => p.Package != null)) {
                AtLocation(parameter.Package, () => {
                    ProcessParameter(parameter);
>>>>>>> master
                });
            }
        }

        private void ProcessParameter(PackageEntry parameter) {
            var files = new List<string>();
            AtLocation("Package", () => {

                // find all files that need to be part of the package
                string folder;
                string filePattern;
                SearchOption searchOption;
                var packageFiles = Path.Combine(Settings.WorkingDirectory, parameter.SourceFilepath);
                if((packageFiles.EndsWith("/", StringComparison.Ordinal) || Directory.Exists(packageFiles))) {
                    folder = Path.GetFullPath(packageFiles);
                    filePattern = "*";
                    searchOption = SearchOption.AllDirectories;
                } else {
                    folder = Path.GetDirectoryName(packageFiles);
                    filePattern = Path.GetFileName(packageFiles);
                    searchOption = SearchOption.TopDirectoryOnly;
                }
                files.AddRange(Directory.GetFiles(folder, filePattern, searchOption));
                files.Sort();

                // compute MD5 hash for package
                string package;
                using(var md5 = MD5.Create()) {
                    var bytes = new List<byte>();
                    foreach(var file in files) {
                        using(var stream = File.OpenRead(file)) {
                            var relativeFilePath = Path.GetRelativePath(folder, file);
                            bytes.AddRange(Encoding.UTF8.GetBytes(relativeFilePath));
                            var fileHash = md5.ComputeHash(stream);
                            bytes.AddRange(fileHash);
                            if(Settings.VerboseLevel >= VerboseLevel.Detailed) {
                                Console.WriteLine($"... computing md5: {relativeFilePath} => {fileHash.ToHexString()}");
                            }
                        }
                    }
<<<<<<< HEAD
                    package = Path.Combine(Settings.OutputDirectory, $"package_{parameter.Name}_{md5.ComputeHash(bytes.ToArray()).ToHexString()}.zip");
                }

                // create zip package
                Console.WriteLine($"=> Building {parameter.Name} package");
                if(Directory.Exists(Settings.OutputDirectory)) {
                    foreach(var file in Directory.GetFiles(Settings.OutputDirectory, $"package_{parameter.Name}*.zip")) {
=======
                    package = Path.Combine(Settings.OutputDirectory, $"package_{parameter.Package}_{md5.ComputeHash(bytes.ToArray()).ToHexString()}.zip");
                }

                // create zip package
                Console.WriteLine($"=> Building {parameter.Package} package");
                if(Directory.Exists(Settings.OutputDirectory)) {
                    foreach(var file in Directory.GetFiles(Settings.OutputDirectory, $"package_{parameter.Package}*.zip")) {
>>>>>>> master
                        try {
                            File.Delete(file);
                        } catch { }
                    }
                }
                using(var zipArchive = ZipFile.Open(package, ZipArchiveMode.Create)) {
                    foreach(var file in files) {
                        var filename = Path.GetRelativePath(folder, file);
                        zipArchive.CreateEntryFromFile(file, filename);
                    }
                }
                parameter.PackagePath = package;
                parameter.Package["SourcePackageKey"] = $"Modules/{_module.Name}/Assets/{Path.GetFileName(package)}";
            });
        }
    }
}