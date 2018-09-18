参考链接：
js使用protobuf: https://www.cnblogs.com/yswenli/p/7099809.html
canvas画图: https://blog.csdn.net/github_38927899/article/details/77433979
.netcore中使用websocket: http://www.cnblogs.com/xiyin/p/7572223.html


proto 生成命令
protoc Message.proto --csharp_out /Users/apple/sourcecode/netcore/snake_server/snake_server/protocol 

protoc.exe --js_out=import_style=commonjs,binary:. Message.proto
browserify export.js > bundle.js