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
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MindTouch.LambdaSharp.Tool.Internal;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MindTouch.LambdaSharp.Tool {

    public class ModelPreprocessor : AModelProcessor {

        //--- Fields ---
        private string _selector;

        //--- Constructors ---
        public ModelPreprocessor(Settings settings, string sourceFilename) : base(settings, sourceFilename) { }

        //--- Methods ---
        public IParser Preprocess(string source, string selector) {
            _selector = ":" + (selector ?? "Default");
            var inputStream = YamlParser.Parse(source);
            var outputStream = new YamlStream {
                Start = inputStream.Start,
                Documents = inputStream.Documents
                    .Select(inputDocument => Preprocess(inputDocument))
                    .ToList(),
                End = inputStream.End
            };
            var parsingEvents = new List<ParsingEvent>();
            outputStream.AppendTo(parsingEvents);
            return new YamlParsingEventsParser(parsingEvents);
        }

        private YamlDocument Preprocess(YamlDocument inputDocument) {

            // replace choice branches with their respective choices
            return new YamlDocument {
                Start = inputDocument.Start,
                Values = ResolveChoices(inputDocument.Values),
                End = inputDocument.End
            };
        }

        private List<AYamlValue> ResolveChoices(List<AYamlValue> inputValues) {
            if(_selector == null) {
                return inputValues;
            }
            var outputValues = new List<AYamlValue>();
            var counter = 0;
            foreach(var inputValue in inputValues) {
                AtLocation($"[{counter++}]", () => {
                    var outputValue = ResolveChoices(inputValue);
                    if(outputValue != null) {
                        outputValues.Add(outputValue);
                    }
                });
            }
            return outputValues;
        }

        private AYamlValue ResolveChoices(AYamlValue inputValue) {
            switch(inputValue) {
            case YamlMap inputMap: {
                    var outputMap = new YamlMap {
                        Start = inputMap.Start,
                        Entries = new List<KeyValuePair<YamlScalar, AYamlValue>>(),
                        End = inputMap.End
                    };
                    Tuple<string, AYamlValue> choice = null;
                    foreach(var inputEntry in inputMap.Entries) {

                        // entries that start with `:` are considered a conditional based on the current selector
                        if(inputEntry.Key.Scalar.Value.StartsWith(":")) {

                            // check if the key matches the selector or the key is `:Default` and no choice has been made yet
                            if(
                                (inputEntry.Key.Scalar.Value == _selector)
                                || (
                                    (inputEntry.Key.Scalar.Value == ":Default")
                                    && (choice == null)
                                )
                            ) {
                                choice = Tuple.Create(
                                    inputEntry.Key.Scalar.Value,
                                    AtLocation(inputEntry.Key.Scalar.Value, () => ResolveChoices(inputEntry.Value), inputEntry.Value)
                                );
                            }
                        } else {

                            // add the entry to the output map
                            outputMap.Entries.Add(new KeyValuePair<YamlScalar, AYamlValue>(
                                inputEntry.Key,
                                AtLocation(inputEntry.Key.Scalar.Value, () => ResolveChoices(inputEntry.Value), inputEntry.Value)
                            ));
                        }
                    }

                    // check if a choice was found
                    if(choice != null) {

                        // check if the input map had no other keys; in the case, just return the choice value
                        if(!outputMap.Entries.Any()) {
                            return choice.Item2;
                        }

                        // otherwise, embed the choice into output map
                        AtLocation(choice.Item1, () => {
                            if(choice.Item2 is YamlMap choiceMap) {
                                foreach(var choiceEntry in choiceMap.Entries) {
                                    outputMap.Entries.Add(choiceEntry);
                                }
                            } else {
                                AddError("choice value is not a map");
                            }
                        });
                    }
                    return outputMap;
                }
            case YamlScalar inputScalar:
                return inputScalar;
            case YamlSequence inputSequence:
                return new YamlSequence {
                    Start = inputSequence.Start,
                    Values = ResolveChoices(inputSequence.Values),
                    End = inputSequence.End
                };
            default:
                AddError($"unrecognized YAML value ({inputValue?.GetType().Name ?? "<null>"})");
                return inputValue;
            }
        }
    }
}