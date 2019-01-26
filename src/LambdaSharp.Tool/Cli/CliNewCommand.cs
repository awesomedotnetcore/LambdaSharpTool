/*
 * MindTouch λ#
 * Copyright (C) 2006-2018 MindTouch, Inc.
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
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;
using LambdaSharp.Tool.Cli.Build;

namespace LambdaSharp.Tool.Cli {

    public class CliNewCommand : ACliCommand {

        //--- Methods --
        public void Register(CommandLineApplication app) {
            app.Command("new", cmd => {
                cmd.HelpOption();
                cmd.Description = "Create new LambdaSharp module or function";

                // function sub-command
                cmd.Command("function", subCmd => {
                    subCmd.HelpOption();
                    subCmd.Description = "Create new LambdaSharp function";

                    // sub-command options
                    var namespaceOption = subCmd.Option("--namespace|-ns <NAME>", "(optional) Root namespace for project (default: same as function name)", CommandOptionType.SingleValue);
                    var directoryOption = subCmd.Option("--working-directory|-wd <PATH>", "(optional) New function project parent directory (default: current directory)", CommandOptionType.SingleValue);
                    var frameworkOption = subCmd.Option("--framework|-f <NAME>", "(optional) Target .NET framework (default: 'netcoreapp2.1')", CommandOptionType.SingleValue);
                    var languageOption = subCmd.Option("--language|-l <LANGUAGE>", "(optional) Select programming language for generated code (default: csharp)", CommandOptionType.SingleValue);
                    var inputFileOption = cmd.Option("--input <FILE>", "(optional) File path to YAML module definition (default: Module.yml)", CommandOptionType.SingleValue);
                    inputFileOption.ShowInHelpText = false;
                    var useProjectReferenceOption = subCmd.Option("--use-project-reference", "(optional) Reference LambdaSharp libraries using a project reference (default behavior when LAMBDASHARP environment variable is set)", CommandOptionType.NoValue);
                    var useNugetReferenceOption = subCmd.Option("--use-nuget-reference", "(optional) Reference LambdaSharp libraries using nuget references", CommandOptionType.NoValue);
                    var cmdArgument = subCmd.Argument("<NAME>", "Name of new project (e.g. MyFunction)");
                    subCmd.OnExecute(() => {
                        Console.WriteLine($"{app.FullName} - {cmd.Description}");
                        var lambdasharpDirectory = Environment.GetEnvironmentVariable("LAMBDASHARP");

                        // validate project vs. nuget reference options
                        bool useProjectReference;
                        if(useProjectReferenceOption.HasValue() && useNugetReferenceOption.HasValue()) {
                            AddError("cannot use --use-project-reference and --use-nuget-reference at the same time");
                            return;
                        }
                        if(useProjectReferenceOption.HasValue()) {
                            if(lambdasharpDirectory == null) {
                                AddError("missing LAMBDASHARP environment variable");
                                return;
                            }
                            useProjectReference = true;
                        } else if(useNugetReferenceOption.HasValue()) {
                            useProjectReference = false;
                        } else if(lambdasharpDirectory != null) {
                            useProjectReference = true;
                        } else {
                            useProjectReference = false;
                        }

                        // TODO (2018-09-13, bjorg): allow following settings to be configurable via command line options
                        var functionMemory = 128;
                        var functionTimeout = 30;

                        // determine function name
                        string functionName;
                        if(cmdArgument.Values.Any()) {
                            functionName = cmdArgument.Values.First();
                        } else {
                            AddError("missing function name argument");
                            return;
                        }
                        var workingDirectory = Path.GetFullPath(directoryOption.Value() ?? Directory.GetCurrentDirectory());
                        NewFunction(
                            lambdasharpDirectory,
                            functionName,
                            namespaceOption.Value(),
                            frameworkOption.Value() ?? "netcoreapp2.1",
                            useProjectReference,
                            workingDirectory,
                            Path.Combine(workingDirectory, inputFileOption.Value() ?? "Module.yml"),
                            languageOption.Value() ?? "csharp",
                            functionMemory,
                            functionTimeout
                        );
                    });
                });

                // module sub-command
                cmd.Command("module", subCmd => {
                    subCmd.HelpOption();
                    subCmd.Description = "Create new LambdaSharp module";

                    // sub-command options
                    var nameOption = subCmd.Option("--name|-n <NAME>", "Name of new module (e.g. My.NewModule)", CommandOptionType.SingleValue);
                    nameOption.ShowInHelpText = false;
                    var directoryOption = subCmd.Option("--working-directory|-wd <PATH>", "(optional) New module directory (default: current directory)", CommandOptionType.SingleValue);
                    var cmdArgument = subCmd.Argument("<NAME>", "Name of new module (e.g. My.NewModule)");
                    subCmd.OnExecute(() => {
                        Console.WriteLine($"{app.FullName} - {cmd.Description}");
                        if(cmdArgument.Values.Any() && nameOption.HasValue()) {
                            AddError("cannot specify --name and an argument at the same time");
                            return;
                        }
                        string moduleName;
                        if(nameOption.HasValue()) {
                            moduleName = nameOption.Value();
                        } else if(cmdArgument.Values.Any()) {
                            moduleName = cmdArgument.Values.First();
                        } else {
                            AddError("missing module name argument");
                            return;
                        }

                        // prepend default owner string
                        if(!moduleName.Contains('.')) {
                            moduleName = "Owner." + moduleName;
                        }
                        NewModule(
                            moduleName,
                            Path.GetFullPath(directoryOption.Value() ?? Directory.GetCurrentDirectory())
                        );
                    });
                });

                // show help text if no sub-command is provided
                cmd.OnExecute(() => {
                    Console.WriteLine(cmd.GetHelpText());
                });
            });
        }

        public void NewModule(string moduleName, string moduleDirectory) {
            try {
                Directory.CreateDirectory(moduleDirectory);
            } catch(Exception e) {
                AddError($"unable to create directory '{moduleDirectory}'", e);
                return;
            }
            var moduleFile = Path.Combine(moduleDirectory, "Module.yml");
            if(File.Exists(moduleFile)) {
                AddError($"module definition '{moduleFile}' already exists");
                return;
            }
            try {
                var module = ReadResource("NewModule.yml", new Dictionary<string, string> {
                    ["MODULENAME"] = moduleName
                });
                File.WriteAllText(moduleFile, module);
                Console.WriteLine($"Created module definition: {Path.GetRelativePath(Directory.GetCurrentDirectory(), moduleFile)}");
            } catch(Exception e) {
                AddError($"unable to create module definition '{moduleFile}'", e);
            }
        }

        public void NewFunction(
            string lambdasharpDirectory,
            string functionName,
            string rootNamespace,
            string framework,
            bool useProjectReference,
            string workingDirectory,
            string moduleFile,
            string language,
            int functionMemory,
            int functionTimeout
        ) {

            // parse yaml module definition
            if(!File.Exists(moduleFile)) {
                AddError($"could not find module '{moduleFile}'");
                return;
            }
            var moduleContents = File.ReadAllText(moduleFile);
            var module = new ModelYamlToAstConverter(new Settings(), moduleFile).Parse(moduleContents);
            if(HasErrors) {
                return;
            }

            // set default namespace if none is set
            if(rootNamespace == null) {
                rootNamespace = $"{module.Module}.{functionName}";
            }

            // create directory for function project
            var projectDirectory = Path.Combine(workingDirectory, functionName);
            if(Directory.Exists(projectDirectory)) {
                AddError($"project directory '{projectDirectory}' already exists");
                return;
            }
            try {
                Directory.CreateDirectory(projectDirectory);
            } catch(Exception e) {
                AddError($"unable to create directory '{projectDirectory}'", e);
                return;
            }

            // create function file
            switch(language) {
            case "csharp":
                NewCSharpFunction(
                    lambdasharpDirectory,
                    functionName,
                    rootNamespace,
                    framework,
                    useProjectReference,
                    workingDirectory,
                    moduleFile,
                    functionMemory,
                    functionTimeout,
                    projectDirectory
                );
                break;
            case "javascript":
                NewJavascriptFunction(
                    lambdasharpDirectory,
                    functionName,
                    rootNamespace,
                    framework,
                    useProjectReference,
                    workingDirectory,
                    moduleFile,
                    functionMemory,
                    functionTimeout,
                    projectDirectory
                );
                break;
            }

            // update YAML module definition
            var moduleLines = File.ReadAllLines(moduleFile).ToList();

            // check if `Items:` section needs to be added
            var functionsIndex = moduleLines.FindIndex(line => line.StartsWith("Items:", StringComparison.Ordinal));
            if(functionsIndex < 0) {

                // add empty separator line if the last line of the file is not empty
                if(moduleLines.Any() && (moduleLines.Last().Trim() != "")) {
                    moduleLines.Add("");
                }
                functionsIndex = moduleLines.Count;
                moduleLines.Add("Items:");
            }
            ++functionsIndex;

            // insert function definition
            moduleLines.InsertRange(functionsIndex, new[] {
                "",
                $"  - Function: {functionName}",
                $"    Description: TODO - update {functionName} description",
                $"    Memory: {functionMemory}",
                $"    Timeout: {functionTimeout}",
            });
            File.WriteAllLines(moduleFile, moduleLines);
        }

        public void NewCSharpFunction(
            string lambdasharpDirectory,
            string functionName,
            string rootNamespace,
            string framework,
            bool useProjectReference,
            string workingDirectory,
            string moduleFile,
            int functionMemory,
            int functionTimeout,
            string projectDirectory
        ) {

            // create function project
            var projectFile = Path.Combine(projectDirectory, functionName + ".csproj");
            var substitutions = new Dictionary<string, string> {
                ["FRAMEWORK"] = framework,
                ["ROOTNAMESPACE"] = rootNamespace,
                ["LAMBDASHARP_PROJECT"] = useProjectReference
                    ? Path.GetRelativePath(projectDirectory, Path.Combine(lambdasharpDirectory, "src", "LambdaSharp", "LambdaSharp.csproj"))
                    : "(not used)",
                ["LAMBDASHARP_VERSION"] = Version.GetWildcardVersion()
            };
            try {
                var projectContents = ReadResource(
                    useProjectReference
                        ? "NewCSharpFunctionProjectLocal.xml"
                        : "NewCSharpFunctionProjectNuget.xml",
                    substitutions
                );
                File.WriteAllText(projectFile, projectContents);
                Console.WriteLine($"Created project file: {Path.GetRelativePath(Directory.GetCurrentDirectory(), projectFile)}");
            } catch(Exception e) {
                AddError($"unable to create project file '{projectFile}'", e);
                return;
            }

            // create function source code
            var functionFile = Path.Combine(projectDirectory, "Function.cs");
            var functionContents = ReadResource("NewCSharpFunction.txt", substitutions);
            try {
                File.WriteAllText(functionFile, functionContents);
                Console.WriteLine($"Created function file: {Path.GetRelativePath(Directory.GetCurrentDirectory(), functionFile)}");
            } catch(Exception e) {
                AddError($"unable to create function file '{functionFile}'", e);
                return;
            }
        }

        public void NewJavascriptFunction(
            string lambdasharpDirectory,
            string functionName,
            string rootNamespace,
            string framework,
            bool useProjectReference,
            string workingDirectory,
            string moduleFile,
            int functionMemory,
            int functionTimeout,
            string projectDirectory
        ) {

            // create function source code
            var functionFile = Path.Combine(projectDirectory, "index.js");
            var functionContents = ReadResource("NewJSFunction.txt");
            try {
                File.WriteAllText(functionFile, functionContents);
                Console.WriteLine($"Created function file: {Path.GetRelativePath(Directory.GetCurrentDirectory(), functionFile)}");
            } catch(Exception e) {
                AddError($"unable to create function file '{functionFile}'", e);
                return;
            }
        }
    }
}
