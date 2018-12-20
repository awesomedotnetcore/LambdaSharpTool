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
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MindTouch.LambdaSharp.Tool.Cli {

    public class CliUtilCommand : ACliCommand {

        //--- Methods --
        public void Register(CommandLineApplication app) {
            app.Command("refresh-spec", cmd => {
                cmd.HelpOption();
                cmd.Description = "Download CloudFormation JSON Specification";
                cmd.ShowInHelpText = false;

                // init options
                cmd.OnExecute(async () => {
                    Console.WriteLine($"{app.FullName} - {cmd.Description}");

                    // determine destination folder
                    var lambdaSharpFolder = Environment.GetEnvironmentVariable("LAMBDASHARP");
                    if(lambdaSharpFolder == null) {
                        AddError("LAMBDASHARP environment variable is not defined");
                        return;
                    }
                    var destinationZipLocation = Path.Combine(lambdaSharpFolder, "src/MindTouch.LambdaSharp.Tool/Resources/CloudFormationResourceSpecification.json.gz");
                    var destinationJsonLocation = Path.Combine(lambdaSharpFolder, "src/MindTouch.LambdaSharp.Tool/Docs/CloudFormationResourceSpecification.json");

                    // determine if we want to install modules from a local check-out
                    await Refresh(
                        "https://d1uauaxba7bl26.cloudfront.net/latest/gzip/CloudFormationResourceSpecification.json",
                        destinationZipLocation,
                        destinationJsonLocation
                    );
                });
            });
        }

        public async Task Refresh(
            string specifcationUrl,
            string destinationZipLocation,
            string destinationJsonLocation
        ) {
            Console.WriteLine();

            // download json specification
            Console.WriteLine($"Fetching specification from {specifcationUrl}");
            var response = await new HttpClient().GetAsync(specifcationUrl);
            string text;
            using(var decompressionStream = new GZipStream(await response.Content.ReadAsStreamAsync(), CompressionMode.Decompress))
            using(var decompressedMemoryStream = new MemoryStream()) {
                await decompressionStream.CopyToAsync(decompressedMemoryStream);
                text = Encoding.UTF8.GetString(decompressedMemoryStream.ToArray());
            }

            // strip all "Documentation" fields to reduce document size
            Console.WriteLine($"Original size: {text.Length:N0}");
            var json = JObject.Parse(text);
            json.Descendants()
                .OfType<JProperty>()
                .Where(attr => attr.Name == "Documentation")
                .ToList()
                .ForEach(attr => attr.Remove());
            text = json.ToString();
            Console.WriteLine($"Stripped size: {text.Length:N0}");
            File.WriteAllText(destinationJsonLocation, json.ToString(Formatting.Indented).Replace("\r\n", "\n"));

            // save compressed file
            using(var fileStream = File.OpenWrite(destinationZipLocation)) {
            using(var compressionStream = new GZipStream(fileStream, CompressionLevel.Optimal))
            using(var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(text)))
                await memoryStream.CopyToAsync(compressionStream);
            }
            var info = new FileInfo(destinationZipLocation);
            Console.WriteLine($"Stored compressed spec file {destinationZipLocation}");
            Console.WriteLine($"Compressed file size: {info.Length:N0}");
        }
    }
}
