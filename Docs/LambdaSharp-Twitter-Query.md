![λ#](LambdaSharp_v2_small.png)

# Module: LambdaSharp.Twitter.Query
_Version:_ 0.5-WIP

## Overview

The `LambdaSharp.Twitter.Query` module conducts a Twitter search at regular intervals and publishes found tweets to a dedicated SNS topic.

__Topics__
* [Resource Types](#resource-types)
* [Parameters](#parameters)
* [Outputs](#outputs)

## Resource Types

This module defines no resource types.

## Parameters

<dl>

<dt><code>TwitterApiKey</code></dt>
<dd>
The <code>TwitterApiKey</code> parameter contains the encrypted Twitter API key.

<i>Required</i>: Yes

<i>Type:</i> Secret
</dd>

<dt><code>TwitterApiSecretKey</code></dt>
<dd>
The <code>TwitterApiSecretKey</code> parameter contains the encrypted Twitter secret API key.

<i>Required</i>: Yes

<i>Type:</i> Secret
</dd>

<dt><code>TwitterQuery</code></dt>
<dd>
The <code>TwitterQuery</code> parameter contains Twitter query for finding tweets

<i>Required</i>: Yes

<i>Type:</i> String
</dd>

<dt><code>TwitterQueryInterval</code></dt>
<dd>
The <code>TwitterQueryInterval</code> parameter contains the interval duration in minutes for conducting new searches.

<i>Required</i>: No (Default: 60)

<i>Type:</i> Number (between 2 and 1,440)
</dd>

</dl>

## Output Values

<dl>

<dt><code>Topic</code></dt>
<dd>
The <code>Topic</code> output contains the ARN of the SNS topic to which tweets that match the <code>TwitterQuery</code> are published to.

<i>Type:</i> AWS::SNS::Topic
</dd>

</dl>

