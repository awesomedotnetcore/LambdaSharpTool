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

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.Lambda.Core;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Tweetinvi;
using Tweetinvi.Models;
using Tweetinvi.Parameters;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.Json.JsonSerializer))]

namespace LambdaSharp.Twitter.Query {
    using TwitterSearch = Tweetinvi.Search;

    public class Function : ALambdaFunction<LambdaScheduleEvent, string> {

        //--- Fields ---
        private string _twitterSearchQuery;
        private IAmazonDynamoDB _dynamoClient;
        private Table _table;
        private IAmazonSimpleNotificationService _snsClient;
        private string _notificationTopic;

        //--- Methods ---
        public override async Task InitializeAsync(LambdaConfig config) {

            // initialize twitter client
            Auth.SetApplicationOnlyCredentials(
                config.ReadText("TwitterApiKey"),
                config.ReadText("TwitterApiSecretKey"),
                true
            );
            _twitterSearchQuery = config.ReadText("TwitterQuery");

            // initialize DynamoDB table
            _dynamoClient = new AmazonDynamoDBClient();
            _table = Table.LoadTable(_dynamoClient, config.ReadDynamoDBTableName("Table"));

            // initialize SNS client
            _snsClient = new AmazonSimpleNotificationServiceClient();
            _notificationTopic = config.ReadText("Topic");
        }

        public override async Task<string> ProcessMessageAsync(LambdaScheduleEvent request, ILambdaContext context) {
            var lastId = 0L;

            // read last_id from table
            var document = await _table.GetItemAsync("last");
            if(
                (document != null)
                && document.TryGetValue("Query", out DynamoDBEntry queryEntry)
                && (queryEntry.AsString() == _twitterSearchQuery)
                && document.TryGetValue("LastId", out DynamoDBEntry lastIdEntry)
            ) {
                lastId = lastIdEntry.AsLong();
            }

            // query for tweets since last id
            LogInfo($"searching for tweets: query='{_twitterSearchQuery}', last_id={lastId}");
            var tweets = TwitterSearch.SearchTweets(new SearchTweetsParameters(_twitterSearchQuery) {
                TweetSearchType = TweetSearchType.OriginalTweetsOnly,
                SinceId = lastId
            }) ?? Enumerable.Empty<ITweet>();

            // check if any tweets were found
            LogInfo($"found {tweets.Count():N0} tweets");
            if(tweets.Any()) {

                // store updated last_id
                await _table.PutItemAsync(new Document {
                    ["Id"] = "last",
                    ["LastId"] = tweets.Max(tweet => tweet.Id),
                    ["Query"] = _twitterSearchQuery
                });

                // send all tweets to topic
                var tasks = new List<Task>();
                foreach(var tweet in tweets) {
                    var json = tweet.ToJson();
                    LogInfo($"sending tweet #{tasks.Count + 1}: {tweet.FullText}\n{json}");
                    tasks.Add(_snsClient.PublishAsync(new PublishRequest {
                        TopicArn = _notificationTopic,
                        Message = json
                    }));
                }

                // wait for all tasks to finish before exiting
                LogInfo($"waiting for all tweets to be sent");
                await Task.WhenAll(tasks.ToArray());
                LogInfo($"all done");
            }
            return "Ok";
        }
    }
}
