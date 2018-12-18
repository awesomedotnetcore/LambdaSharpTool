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
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using MindTouch.LambdaSharp.Tool.Internal;
using MindTouch.LambdaSharp.Tool.Model;
using Mono.Cecil;

namespace MindTouch.LambdaSharp.Tool.Cli.Build {

    public class ModelFunctionPackager : AModelProcessor {

        //--- Constants ---
        private const string GITSHAFILE = "gitsha.txt";

        //--- Types ---
        private class CustomAssemblyResolver : BaseAssemblyResolver {

            //--- Fields ---
            private string _directory;
            private List<AssemblyDefinition> _loadedAssemblies = new List<AssemblyDefinition>();

            //--- Constructors ---
            public CustomAssemblyResolver(string directory) {
                _directory = directory;
            }

            //--- Methods ---
            public override AssemblyDefinition Resolve(AssemblyNameReference name) {
                var assembly = AssemblyDefinition.ReadAssembly(Path.Combine(_directory, $"{name.Name}.dll"), new ReaderParameters {
                    AssemblyResolver = this
                });
                if(assembly != null) {
                    _loadedAssemblies.Add(assembly);
                }
                return assembly;
            }

            protected override void Dispose(bool disposing) {
                base.Dispose(disposing);
                if(disposing) {
                    foreach(var assembly in _loadedAssemblies) {
                        assembly.Dispose();
                    }
                }
            }
        }

        //--- Fields ---
        private ModuleBuilder _builder;

        //--- Constructors ---
        public ModelFunctionPackager(Settings settings, string sourceFilename) : base(settings, sourceFilename) { }

        public void Package(
            ModuleBuilder builder,
            bool skipCompile,
            bool skipAssemblyValidation,
            string gitsha,
            string buildConfiguration
        ) {
            _builder = builder;

            // delete old packages
            if(Directory.Exists(Settings.OutputDirectory)) {
                foreach(var file in Directory.GetFiles(Settings.OutputDirectory, $"function_*.zip")) {
                    try {
                        File.Delete(file);
                    } catch { }
                }
            }
            foreach(var function in builder.Entries.OfType<FunctionEntry>()) {
                AtLocation(function.FullName, () => {
                    Process(
                        function,
                        skipCompile,
                        skipAssemblyValidation,
                        gitsha,
                        buildConfiguration
                    );
                });
            }
        }

        private void Process(
            FunctionEntry function,
            bool skipCompile,
            bool skipAssemblyValidation,
            string gitsha,
            string buildConfiguration
        ) {
            switch(Path.GetExtension(function.Project).ToLowerInvariant()) {
            case "":

                // inline project; nothing to do
                break;
            case ".csproj":
                ProcessDotNet(
                    function,
                    skipCompile,
                    skipAssemblyValidation,
                    gitsha,
                    buildConfiguration
                );
                break;
            case ".js":
                ProcessJavascript(
                    function,
                    skipCompile,
                    skipAssemblyValidation,
                    gitsha,
                    buildConfiguration
                );
                break;
            default:
                AddError("could not determine the function language");
                return;
            }
        }

        private void ProcessDotNet(
            FunctionEntry function,
            bool skipCompile,
            bool skipAssemblyValidation,
            string gitsha,
            string buildConfiguration
        ) {
            function.Language = "csharp";

            // compile function project
            var projectName = Path.GetFileNameWithoutExtension(function.Project);
            var csproj = XDocument.Load(function.Project);
            var mainPropertyGroup = csproj.Element("Project")?.Element("PropertyGroup");
            var targetFramework = mainPropertyGroup?.Element("TargetFramework").Value;

            // validate the project is using the most recent lambdasharp assembly references
            if(!skipAssemblyValidation && function.HasAssemblyValidation) {
                var includes = csproj.Element("Project")
                    ?.Elements("ItemGroup")
                    .Elements("PackageReference")
                    .Where(elem => elem.Attribute("Include")?.Value.StartsWith("MindTouch.LambdaSharp", StringComparison.Ordinal) ?? false);
                if(includes != null) {
                    foreach(var include in includes) {
                        var expectedVersion = VersionInfo.Parse($"{Settings.ToolVersion.Major}.{Settings.ToolVersion.Minor}{Settings.ToolVersion.Suffix}");
                        var library = include.Attribute("Include").Value;
                        var libraryVersionText = include.Attribute("Version")?.Value;
                        if(libraryVersionText == null) {
                            AddError($"csproj file is missing a version attribute in its assembly reference for {library} (expected version: '{expectedVersion}')");
                        } else if(libraryVersionText.EndsWith("*", StringComparison.Ordinal)) {
                            if(!VersionInfo.TryParse(libraryVersionText.Substring(0, libraryVersionText.Length - 1), out VersionInfo libraryVersion)) {
                                AddError($"csproj file contains an invalid wildcard version in its assembly reference for {library} (expected version: '{expectedVersion}', found: '{libraryVersionText}')");
                            } else if(!libraryVersion.IsCompatibleWith(expectedVersion)) {
                                AddError($"csproj file contains a mismatched assembly reference for {library} (expected version: '{expectedVersion}', found: '{libraryVersionText}')");
                            }
                        } else if(!VersionInfo.TryParse(libraryVersionText, out VersionInfo libraryVersion)) {
                            AddError($"csproj file contains an invalid version in its assembly reference for {library} (expected version: '{expectedVersion}', found: '{libraryVersionText}')");
                        } else if(!libraryVersion.IsCompatibleWith(expectedVersion)) {
                            AddError($"csproj file contains a mismatched assembly reference for {library} (expected version: '{expectedVersion}', found: '{libraryVersionText}')");
                        }
                    }
                    if(Settings.HasErrors) {
                        return;
                    }
                }
            }
            if(skipCompile) {
                return;
            }

            // dotnet tools have to be run from the project folder; otherwise specialized tooling is not picked up from the .csproj file
            var projectDirectory = Path.Combine(Settings.WorkingDirectory, projectName);
            Console.WriteLine($"=> Building function {function.Name} [{targetFramework}, {buildConfiguration}]");

            // restore project dependencies
            if(!DotNetRestore(projectDirectory)) {
                AddError("`dotnet restore` command failed");
                return;
            }

            // compile project
            var dotnetOutputPackage = Path.Combine(Settings.OutputDirectory, function.Name + ".zip");
            if(!DotNetLambdaPackage(targetFramework, buildConfiguration, dotnetOutputPackage, projectDirectory)) {
                AddError("`dotnet lambda package` command failed");
                return;
            }

            // check if the project zip file was created
            if(!File.Exists(dotnetOutputPackage)) {
                AddError($"could not find project package: {dotnetOutputPackage}");
                return;
            }

            // decompress project zip into temporary folder so we can add the `GITSHAFILE` files
            var tempDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            try {

                // extract existing package into temp folder
                if(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                    ZipFile.ExtractToDirectory(dotnetOutputPackage, tempDirectory);
                    File.Delete(dotnetOutputPackage);
                } else {
                    Directory.CreateDirectory(tempDirectory);
                    if(!UnzipWithTool(dotnetOutputPackage, tempDirectory)) {
                        AddError("`unzip` command failed");
                        return;
                    }
                }

                // verify the function handler can be found in the compiled assembly
                if(function.HasHandlerValidation) {
                    if(function.Function.Handler is string handler) {
                        ValidateEntryPoint(tempDirectory, handler);
                    }
                }
                var package = CreatePackage(function.Name, gitsha, tempDirectory);
                _builder.AddAsset($"{function.FullName}::PackageName", package);
            } finally {
                if(Directory.Exists(tempDirectory)) {
                    try {
                        Directory.Delete(tempDirectory, recursive: true);
                    } catch {
                        Console.WriteLine($"WARNING: clean-up failed for temporary directory: {tempDirectory}");
                    }
                }
            }
        }

        private bool DotNetRestore(string projectDirectory) {
            var dotNetExe = ProcessLauncher.DotNetExe;
            if(string.IsNullOrEmpty(dotNetExe)) {
                AddError("failed to find the \"dotnet\" executable in path.");
                return false;
            }
            return ProcessLauncher.Execute(
                dotNetExe,
                new[] { "restore" },
                projectDirectory,
                Settings.VerboseLevel >= VerboseLevel.Detailed
            );
        }

        private bool DotNetLambdaPackage(string targetFramework, string buildConfiguration, string outputPackagePath, string projectDirectory) {
            var dotNetExe = ProcessLauncher.DotNetExe;
            if(string.IsNullOrEmpty(dotNetExe)) {
                AddError("failed to find the \"dotnet\" executable in path.");
                return false;
            }
            return ProcessLauncher.Execute(
                dotNetExe,
                new[] { "lambda", "package", "-c", buildConfiguration, "-f", targetFramework, "-o", outputPackagePath },
                projectDirectory,
                Settings.VerboseLevel >= VerboseLevel.Detailed
            );
        }

        private bool ZipWithTool(string zipArchivePath, string zipFolder) {
            var zipTool = ProcessLauncher.ZipExe;
            if(string.IsNullOrEmpty(zipTool)) {
                AddError("failed to find the \"zip\" utility program in path. This program is required to maintain Linux file permissions in the zip archive.");
                return false;
            }
            return ProcessLauncher.Execute(
                zipTool,
                new[] { "-r", zipArchivePath, "." },
                zipFolder,
                Settings.VerboseLevel >= VerboseLevel.Detailed
            );
        }

        private bool UnzipWithTool(string zipArchivePath, string unzipFolder) {
            var unzipTool = ProcessLauncher.UnzipExe;
            if(unzipTool == null) {
                AddError("failed to find the \"unzip\" utility program in path. This program is required to maintain Linux file permissions in the zip archive.");
                return false;
            }
            return ProcessLauncher.Execute(
                unzipTool,
                new[] { zipArchivePath, "-d", unzipFolder },
                Directory.GetCurrentDirectory(),
                Settings.VerboseLevel >= VerboseLevel.Detailed
            );
        }

        private void ProcessJavascript(
            FunctionEntry function,
            bool skipCompile,
            bool skipAssemblyValidation,
            string gitsha,
            string buildConfiguration
        ) {
            if(skipCompile) {
                return;
            }
            Console.WriteLine($"=> Building function {function.Name} [{function.Function.Runtime}]");
            var package = CreatePackage(function.Name, gitsha, Path.GetDirectoryName(function.Project));
            _builder.AddAsset($"{function.FullName}::PackageName", package);
        }

        private string CreatePackage(string functionName, string gitsha, string folder) {
            string package;

            // add `gitsha.txt` if GitSha is supplied
            if(gitsha != null) {
                File.WriteAllText(Path.Combine(folder, GITSHAFILE), gitsha);
            }

            // compress temp folder into new package
            var zipTempPackage = Path.GetTempFileName() + ".zip";
            if(File.Exists(zipTempPackage)) {
                File.Delete(zipTempPackage);
            }

            // compute MD5 hash for lambda function
            var files = new List<string>();
            using(var md5 = MD5.Create()) {
                var bytes = new List<byte>();
                files.AddRange(Directory.GetFiles(folder, "*", SearchOption.AllDirectories));
                files.Sort();
                foreach(var file in files) {
                    var relativeFilePath = Path.GetRelativePath(folder, file);
                    var filename = Path.GetFileName(file);

                    // don't include the `gitsha.txt` since it changes with every build
                    if(filename != GITSHAFILE) {
                        using(var stream = File.OpenRead(file)) {
                            bytes.AddRange(Encoding.UTF8.GetBytes(relativeFilePath));
                            var fileHash = md5.ComputeHash(stream);
                            bytes.AddRange(fileHash);
                            if(Settings.VerboseLevel >= VerboseLevel.Detailed) {
                                Console.WriteLine($"... computing md5: {relativeFilePath} => {fileHash.ToHexString()}");
                            }
                        }
                    }
                }
                package = Path.Combine(Settings.OutputDirectory, $"function_{functionName}_{md5.ComputeHash(bytes.ToArray()).ToHexString()}.zip");
            }

            // compress folder contents
            if(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                using(var zipArchive = ZipFile.Open(zipTempPackage, ZipArchiveMode.Create)) {
                    foreach(var file in files) {
                        var filename = Path.GetRelativePath(folder, file);
                        zipArchive.CreateEntryFromFile(file, filename);
                    }
                }
            } else {
                if(!ZipWithTool(zipTempPackage, folder)) {
                    AddError("`zip` command failed");
                    return null;
                }
            }
            if(gitsha != null) {
                try {
                    File.Delete(Path.Combine(folder, GITSHAFILE));
                } catch { }
            }
            if(!Directory.Exists(Settings.OutputDirectory)) {
                Directory.CreateDirectory(Settings.OutputDirectory);
            }
            File.Move(zipTempPackage, package);
            return package;
        }

        private void ValidateEntryPoint(string directory, string handler) {
            var parts = handler.Split("::");
            if(parts.Length != 3) {
                AddError("'Handler' attribute has invalid value");
                return;
            }
            try {
                var functionAssemblyName = parts[0];
                var functionClassName = parts[1];
                var functionMethodName = parts[2];
                using(var resolver = new CustomAssemblyResolver(directory))
                using(var functionAssembly = AssemblyDefinition.ReadAssembly(Path.Combine(directory, $"{functionAssemblyName}.dll"), new ReaderParameters {
                    AssemblyResolver = resolver
                })) {
                    if(functionAssembly == null) {
                        AddError("could not load assembly");
                        return;
                    }
                    var functionClassType = functionAssembly.MainModule.GetType(functionClassName);
                    if(functionClassType == null) {
                        AddError($"could not find type '{functionClassName}' in assembly");
                        return;
                    }
                again:
                    var functionMethod = functionClassType.Methods.FirstOrDefault(method => method.Name == functionMethodName);
                    if(functionMethod == null) {
                        if(functionClassType.BaseType == null) {
                            AddError($"could not find method '{functionMethodName}' in type '{functionClassName}'");
                            return;
                        }
                        functionClassType = functionClassType.BaseType.Resolve();
                        goto again;
                    }
                }
            } catch(Exception e) {
                if(Settings.VerboseLevel >= VerboseLevel.Exceptions) {
                    AddError(e);
                } else {
                    Console.WriteLine("WARNING: unable to validate function entry-point due to an internal error");
                }
            }
        }
    }
}