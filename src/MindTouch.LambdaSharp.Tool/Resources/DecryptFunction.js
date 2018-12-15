'use strict';

var AWS = require('aws-sdk');
var response = require('cfn-response');
var kms = new AWS.KMS();

exports.handler = function(event, context) {
    try {
        console.log('*** INFO: ' + JSON.stringify(event));
        if(event.RequestType == 'Delete') {
            response.send(event, context, response.SUCCESS);
        } else {
            kms.decrypt({
                CiphertextBlob: new Buffer(event.ResourceProperties.Ciphertext, 'base64')
            }, function(err, result) {
                if(err) {
                    response.send(event, context, response.FAILED);
                } else {
                    response.send(event, context, response.SUCCESS, {
                        Plaintext: result.Plaintext.toString('utf8')
                    }, 'decrypted:' + event.LogicalResourceId, true);
                }
            });
        }
    } catch(err) {
        console.log('*** ERROR: ' + JSON.stringify(err));
        response.send(event, context, response.FAILED);
    }
};
