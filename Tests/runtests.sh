lash() {
    rm Results/$1-CF.json > /dev/null 2>&1
    dotnet $LAMBDASHARP/src/MindTouch.LambdaSharp.Tool/bin/Debug/netcoreapp2.1/MindTouch.LambdaSharp.Tool.dll deploy \
        --verbose:exceptions \
        --tier Test \
        --cf-output Results/$1-CF.json \
        --dryrun:cloudformation \
        --aws-account-id 123456789012 \
        --aws-region us-east-1 \
        --gitsha 0123456789ABCDEF0123456789ABCDEF01234567 \
        --runtime-version 0.5-WIP \
        --cli-version 0.5-WIP \
        --deployment-bucket-name lambdasharp-bucket-name \
        --deployment-notifications-topic-arn  arn:aws:sns:us-east-1:123456789012:LambdaSharp-DeploymentNotificationTopic \
        --skip-dependency-validation \
        $1.yml
}

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
        --deployment-notifications-topic-arn  arn:aws:sns:us-east-1:123456789012:LambdaSharp-DeploymentNotificationTopic

    if [ $? -ne 0 ]; then
        exit $?
    fi
    lash Empty
    lash Empty-NoLambdaSharpDependencies
    lash Empty-NoModuleRegistration
    lash Function
    lash Function-NoLambdaSharpDependencies
    lash Function-NoModuleRegistration
    lash Function-NoFunctionRegistration
    lash Fn-Base64
    lash Fn-Cidr
    lash Fn-GetAtt
    lash Fn-GetAZs
    lash Fn-ImportValue
    lash Fn-Join
    lash Fn-Ref
    lash Fn-Select
    lash Fn-Split
    lash Fn-Sub
    lash Fn-Transform
    lash Source-Topic
    lash Source-Timer
    lash Source-Api-SlackCommand
    lash Source-Api-RequestResponse
    lash Source-S3
    lash Source-Sqs
    lash Source-Alexa
    lash Variables
    lash Source-DynamoDB
    lash Source-Kinesis
    lash Inputs
    lash Outputs
    lash Outputs-LiteralValue
    lash Package
    lash NestedModule
    lash Parameter-Secret
    lash Variable-Secret
else

    # run requested test
    rm Results/$1-CF.json > /dev/null 2>&1
    dotnet run -p $LAMBDASHARP/src/MindTouch.LambdaSharp.Tool/MindTouch.LambdaSharp.Tool.csproj -- deploy \
        --verbose:exceptions \
        --tier Test \
        --cf-output Results/$1-CF.json \
        --dryrun:cloudformation \
        --aws-account-id 123456789012 \
        --aws-region us-east-1 \
        --gitsha 0123456789ABCDEF0123456789ABCDEF01234567 \
        --runtime-version 0.5-WIP \
        --cli-version 0.5-WIP \
        --deployment-bucket-name lambdasharp-bucket-name \
        --deployment-notifications-topic-arn  arn:aws:sns:us-east-1:123456789012:LambdaSharp-DeploymentNotificationTopic \
        --skip-dependency-validation \
        $1.yml
fi
