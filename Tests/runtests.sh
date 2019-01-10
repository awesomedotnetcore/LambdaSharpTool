if [ -z "$1" ]; then

    # run everything
    dotnet run -p $LAMBDASHARP/src/MindTouch.LambdaSharp.Tool/MindTouch.LambdaSharp.Tool.csproj -- info \
        --verbose:exceptions \
        --tier Test \
        --aws-account-id 123456789012 \
        --aws-region us-east-1 \
        --runtime-version 0.5-WIP \
        --cli-version 0.5-WIP \
        --deployment-bucket-name lambdasharp-bucket-name \
        --deployment-notifications-topic  arn:aws:sns:us-east-1:123456789012:LambdaSharp-DeploymentNotificationTopic

    if [ $? -ne 0 ]; then
        exit $?
    fi

    rm Results/*.json > /dev/null 2>&1
    dotnet $LAMBDASHARP/src/MindTouch.LambdaSharp.Tool/bin/Debug/netcoreapp2.1/MindTouch.LambdaSharp.Tool.dll deploy \
        --verbose:exceptions \
        --tier Test \
        --cfn-output Results/ \
        --dryrun:cloudformation \
        --aws-account-id 123456789012 \
        --aws-region us-east-1 \
        --gitsha 0123456789ABCDEF0123456789ABCDEF01234567 \
        --runtime-version 0.5-WIP \
        --cli-version 0.5-WIP \
        --deployment-bucket-name lambdasharp-bucket-name \
        --deployment-notifications-topic  arn:aws:sns:us-east-1:123456789012:LambdaSharp-DeploymentNotificationTopic \
        --no-dependency-validation \
        Empty.yml \
        Empty-NoLambdaSharpDependencies.yml \
        Empty-NoModuleRegistration.yml \
        Function.yml \
        Function-NoLambdaSharpDependencies.yml \
        Function-NoModuleRegistration.yml \
        Function-NoFunctionRegistration.yml \
        Fn-Base64.yml \
        Fn-Cidr.yml \
        Fn-FindInMap.yml \
        Fn-GetAtt.yml \
        Fn-GetAZs.yml \
        Fn-ImportValue.yml \
        Fn-Join.yml \
        Fn-Ref.yml \
        Fn-Select.yml \
        Fn-Split.yml \
        Fn-Sub.yml \
        Fn-Transform.yml \
        Source-Topic.yml \
        Source-Timer.yml \
        Source-Api-SlackCommand.yml \
        Source-Api-RequestResponse.yml \
        Source-S3.yml \
        Source-Sqs.yml \
        Source-Alexa.yml \
        Variables.yml \
        Source-DynamoDB.yml \
        Source-Kinesis.yml \
        Parameter-String.yml \
        Parameter-Resource.yml \
        Parameter-ConditionalResource.yml \
        Parameter-Secret.yml \
        Import-String.yml \
        Import-Resource.yml \
        Import-ConditionalResource.yml \
        Import-Secret.yml \
        Output-LiteralValue.yml \
        Output-Variable.yml \
        Output-Resource.yml \
        Output-Function.yml \
        Output-CustomResource.yml \
        Output-Macro.yml \
        Package.yml \
        NestedModule.yml \
        Variable-Secret.yml \
        Function-Finalizer.yml \
        Condition-Resource.yml \
        Condition-Inline-Resource.yml \
        Condition-Scoped-Resource.yml \
        Condition-Function.yml \
        Condition-Condition.yml \
        ../Runtime/LambdaSharp.System \
        ../Runtime/LambdaSharp.S3PackageLoader \
        ../Runtime/LambdaSharp.S3Subscriber \
        ../Samples/AlexaSample \
        ../Samples/ApiSample \
        ../Samples/CustomResourceSample \
        ../Samples/DynamoDBSample \
        ../Samples/KinesisSample \
        ../Samples/MacroSample \
        ../Samples/S3Sample \
        ../Samples/ScheduleSample \
        ../Samples/SlackCommandSample \
        ../Samples/SnsSample \
        ../Samples/SqsSample \
        ../Demos/Demo \
        ../Demos/DemoS3BucketSupscription/DemoS3Bucket \
        ../Demos/DemoS3BucketSupscription/DemoS3Subscriber \
        ../Demos/BadModule
else

    # run requested test
    rm Results/$1.json > /dev/null 2>&1
    dotnet run -p $LAMBDASHARP/src/MindTouch.LambdaSharp.Tool/MindTouch.LambdaSharp.Tool.csproj -- deploy \
        --verbose:exceptions \
        --tier Test \
        --cfn-output Results/$1.json \
        --dryrun:cloudformation \
        --aws-account-id 123456789012 \
        --aws-region us-east-1 \
        --gitsha 0123456789ABCDEF0123456789ABCDEF01234567 \
        --runtime-version 0.5-WIP \
        --cli-version 0.5-WIP \
        --deployment-bucket-name lambdasharp-bucket-name \
        --deployment-notifications-topic  arn:aws:sns:us-east-1:123456789012:LambdaSharp-DeploymentNotificationTopic \
        --no-dependency-validation \
        $1.yml
fi
