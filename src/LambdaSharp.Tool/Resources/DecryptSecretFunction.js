const AWS = require('aws-sdk');
const https = require('https');
const url = require('url');
const kms = new AWS.KMS();

var logInfo = message => console.log('*** INFO: ' + message);
var logError = message => console.log('*** ERROR: ' + message);

exports.handler = (event, context) => {
    try {
        logInfo('request: ' + JSON.stringify(event));
        switch(event.RequestType) {
        case 'Create':
        case 'Update':
            kms.decrypt({
                CiphertextBlob: new Buffer(event.ResourceProperties.Ciphertext, 'base64')
            }, (error, result) => {
                if(error.name == 'InvalidCiphertextException') {
                    const message = 'Cipher text is not a valid secret';
                    logError('decrypt failed: ' + message);
                    send(event, context, 'FAILED', null, message);
                } else if(error.name == 'AccessDeniedException') {
                    logError('decrypt failed: ' + error.message);
                    send(event, context, 'FAILED', null, error.message);
                } else if(error) {
                    logError('decrypt failed: ' + error.toString());
                    send(event, context, 'FAILED', null, error.toString());
                } else {
                    send(event, context, 'SUCCESS', {
                        Plaintext: result.Plaintext.toString('utf8')
                    });
                }
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
        logError('internal error: ' + err.message + '\n' + err.stack);
        send(event, context, 'FAILED', null, 'internal error');
    }
};

function send(event, context, status, data, reason) {
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
    const payload = JSON.stringify(body);
    const parsedUrl = url.parse(event.ResponseURL);
    const request = https.request({
        hostname: parsedUrl.hostname,
        port: 443,
        path: parsedUrl.path,
        method: 'PUT',
        headers: {
            'content-type': '',
            'content-length': payload.length
        }
    }, () => {
        context.done();
    });
    request.on('error', error => {
        logError('send(..) failed executing https.request(..): ' + error);
        context.done();
    });
    request.write(payload);
    request.end();
}
