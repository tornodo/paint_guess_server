var wsUri ="ws://localhost:9255/room?key=aaa";

websocket = new WebSocket(wsUri);

websocket.onopen = function(evt) {
    onOpen(evt)
};
websocket.onclose = function(evt) {
    onClose(evt)
};
websocket.onmessage = function(evt) {
    onMessage(evt)
};
websocket.onerror = function(evt) {
    onError(evt)
};

function showLog(log) {
    console.log(log);
}

function onOpen(evt) {
    showLog("CONNECTED");
    var message = new proto.Protocol();
    message.setKey("first");
    message.setName("haidao");
    message.setType(proto.Protocol.ProtocolType.LOGIN);
    var bytes = message.serializeBinary();
    websocket.send(bytes);
}

function onClose(evt) {
    showLog("DISCONNECTED");
}

function onMessage(evt) {
    var data = proto.Protocol.deserializeBinary(evt.data);
    console.log(data);
}

function onError(evt) {
    showLog(evt.data);
}  