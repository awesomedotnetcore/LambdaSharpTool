// TODO: review and condense

'use strict';

var AWS = require('aws-sdk');
var https = require('https');
var url = require('url');
var kms = new AWS.KMS();

var logInfo = message => console.log('*** INFO: ' + message);
var logError = message => console.log('*** ERROR: ' + message);

var send = function(event, context, status, data, reason) {
    var body = {
        Status: status,
        Reason: (status == 'FAILED') ? (reason || 'operation failed') : '',
        PhysicalResourceId: 'decrypted:' + event.LogicalResourceId,
        StackId: event.StackId,
        RequestId: event.RequestId,
        LogicalResourceId: event.LogicalResourceId,
        NoEcho: true,
        Data: '...'
    };
    logInfo('response: ' + JSON.stringify(body));
    body.Data = data;
    var payload = JSON.stringify(body);
    var parsedUrl = url.parse(event.ResponseURL);
    var request = https.request({
        hostname: parsedUrl.hostname,
        port: 443,
        path: parsedUrl.path,
        method: 'PUT',
        headers: {
            'content-type': '',
            'content-length': payload.length
        }
    }, function(response) {
        context.done();
    });
    request.on('error', function(error) {
        logError('send(..) failed executing https.request(..): ' + error);
        context.done();
    });
    request.write(payload);
    request.end();
};

exports.handler = function(event, context) {
    try {
        logInfo('request: ' + JSON.stringify(event));
        switch(event.RequestType) {
        case 'Create':
        case 'Update':
            kms.decrypt({
                CiphertextBlob: new Buffer(event.ResourceProperties.Ciphertext, 'base64')
            }, function(err, result) {
                if(err) {
                    logError('decrypt failed: ' + JSON.stringify(err));
                    send(event, context, 'FAILED', null, err.message);
                    return;
                }
                send(event, context, 'SUCCESS', {
                    Plaintext: result.Plaintext.toString('utf8')
                });
            });
            break;
        case 'Delete':
            send(event, context, 'SUCCESS');
            break;
        default:
            send(event, context, 'FAILED', null, 'unknown request type: ' + event.RequestType);
            break;
        }
    } catch(err) {
        logError('internal error: ' + JSON.stringify(err));
        send(event, context, 'FAILED', null, 'internal error');
    }
};
