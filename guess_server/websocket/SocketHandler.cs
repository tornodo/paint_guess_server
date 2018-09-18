using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using guess_server.log;
using Google.Protobuf;
using guess_server.user;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using System.Diagnostics.Contracts;
using guess_server.db;
using System.Diagnostics;
using System.Linq;
using Microsoft.EntityFrameworkCore;

namespace guess_server.websocket
{
    public class SocketHandler
    {
        private static readonly int CODE_SUCCESS = 0;
        private static readonly int CODE_ERROR = -1;

        private static readonly int BroadcastEmptyId = -1;
        private static readonly string BroadcastEmptyKey = "";

        private static readonly int CanBeginReadyCounts = 2;
        private static readonly int GameTotalCountdown = 120000;
        private static readonly int Countdown = 30000;

        private static GuessDbContext db = new GuessDbContext();
        private static ILogger logger;
        private static readonly int BufferSize = 4096;
        private static readonly int ErrorRetryCounts = 5;
        private static readonly Object roomLock = new object();
        private static readonly ConcurrentDictionary<string, ChatRoom> Rooms = new ConcurrentDictionary<string, ChatRoom>();
        private static readonly ConcurrentDictionary<string, User> Users = new ConcurrentDictionary<string, User>();
        private static readonly CancellationToken token = new CancellationTokenSource().Token;
        
        private class TimerObject
        {
            public Timer timer;
            public ChatRoom room;
        }

        public static async void HandleWebsocket(string key, WebSocket socket)
        {
            logger = Log.CeateLogger(Log.DefaultLogger);
            var buffer = new ArraySegment<byte>(new byte[BufferSize]);
            var ms = new MemoryStream();
            while (true)
            {
                ms.SetLength(0);
                try
                {
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(buffer, token);
                        if (result.MessageType != WebSocketMessageType.Binary)
                        {
                            return;
                        }
                        ms.Write(buffer.Array, buffer.Offset, result.Count);
                    } while (!result.EndOfMessage);
                    logger.LogDebug("buffer.offset " + buffer.Offset.ToString());
                    ms.Seek(0, SeekOrigin.Begin);
                    var p = new Protocol();
                    p.MergeFrom(ms);
                    HandleProtocol(socket, p);
                }
                catch (Exception e)
                {
                    logger.LogError(e.Message);

                    _userOffline(key);
                    // 断开连接
                    await socket.CloseAsync(
                        closeStatus: WebSocketCloseStatus.NormalClosure,
                        statusDescription: "Closed",
                        cancellationToken: CancellationToken.None
                    ).ConfigureAwait(false);
                }
            }
        }
        
        public static void HandleProtocol(WebSocket socket, Protocol protocol)
        {
            switch (protocol.Type)
            {
                case Protocol.Types.ProtocolType.Chat:
                    _handleChat(socket, protocol);
                    break;
                case Protocol.Types.ProtocolType.CreateRoom:
                    _handleCreateRoom(socket, protocol);
                    break;
                case Protocol.Types.ProtocolType.GameBegin:
                    _handleGameBegin(socket, protocol);
                    break;
                case Protocol.Types.ProtocolType.JoinRoom:
                    _handleJoinRoom(socket, protocol);
                    break;
                case Protocol.Types.ProtocolType.LeaveRoom:
                    _handleLeaveRoom(socket, protocol);
                    break;
                case Protocol.Types.ProtocolType.Login:
                    _handleLogin(socket, protocol);
                    break;
                case Protocol.Types.ProtocolType.Paint:
                    _handlePaint(socket, protocol);
                    break;
                default:
                    logger.LogInformation("无效的消息类型", protocol.ToString());
                    break;
            }
        }

        private static bool _hasUser(string key)
        {
            return Users.ContainsKey(key);
        }

        private static bool _hasRoom(string key)
        {
            return Rooms.ContainsKey(key);
        }

        private static User _getUser(string key)
        {
            User user;
            while (!Users.TryGetValue(key, out user))
            {
                logger.LogError("Users TryGetValue error");
                Thread.Sleep(10);
            }
            return user;
        }

        private static ChatRoom _getRoom(string key)
        {
            ChatRoom room;
            while(!Rooms.TryGetValue(key, out room))
            {
                logger.LogError("Rooms TryGetValue error");
                Thread.Sleep(10);
            }
            return room;
        }

        /// <summary>
        /// 登陆服务器
        /// </summary>
        /// <param name="socket"></param>
        /// <param name="protocol"></param>
        private static void _handleLogin(WebSocket socket, Protocol protocol)
        {
            if (!_hasUser(protocol.Key))
            {
                var user = new User(protocol.Key, protocol.Name, socket);
                user.Avatar = protocol.Avatar;
                Users.TryAdd(protocol.Key, user);

                Users u = db.users.Where(us => us.key == protocol.Key).FirstOrDefault();
                if (u == null)
                {
                    var newUser = new Users();
                    newUser.key = protocol.Key;
                    newUser.name = protocol.Name;
                    newUser.create = DateTime.Now.ToString("yyyy:MM:dd HH:mm:ss");
                    db.users.Add(newUser);
                    try
                    {
                        db.SaveChanges();
                    }
                    catch (Exception e)
                    {
                        logger.LogError("保存用户信息出错", e);
                    }
                }
            } else
            {
                User user = _getUser(protocol.Key);
                // 别把已经登陆正在玩游戏的用户挤掉线(同时登陆)
                if (user.Offline)
                {
                    user.Socket = socket;
                    user.Offline = false;
                    ChatRoom room = _getUserRoom(protocol.Key);
                    if (room != null && room.GameBegin)
                    {
                        // 告诉客户端重新进入房间，继续游戏
                        Protocol pro = _newResponseProtocol(protocol.Key, true, protocol.Id);
                        pro.Type = Protocol.Types.ProtocolType.EnteredRoom;
                        pro.Users.Add(room.GetUserInfo());
                        _sendMessage(socket, protocol.Key, pro.ToByteArray());

                        // 通知房间其他玩家，用户重新连接成功
                        pro.Type = Protocol.Types.ProtocolType.Online;
                        pro.Broadcast = true;
                        _broadcastRoomMessage(room, protocol.Key, pro.ToByteArray());
                        return;
                    }
                }
            }
            Protocol p = _newResponseProtocol(protocol.Key, true, protocol.Id);
            _sendMessage(socket, protocol.Key, p.ToByteArray());

            _broadcastRoomInfo(null);
        }

        /// <summary>
        /// 聊天信息处理
        /// </summary>
        /// <param name="socket"></param>
        /// <param name="protocol"></param>
        private static void _handleChat(WebSocket socket, Protocol protocol)
        {
            if (String.IsNullOrWhiteSpace(protocol.RoomKey))
            {
                var proto = _newResponseProtocol(protocol.Key, false, protocol.Id);
                proto.Error = "请先加入房间";
                _sendMessage(socket, protocol.Key, proto.ToByteArray());
                return;
            }

            var content = Regex.Replace(protocol.Message, "</?[^>]+>", "");
            var user = _getUser(protocol.Key);
            if (user == null)
            {
                logger.LogError("key 对应的用户不存在", protocol.ToString());
                return;
            }
            var room = _getRoom(protocol.RoomKey);
            var p = _newResponseProtocol(protocol.Key, true, protocol.Id);
            p.Message = content;
            if (room != null)
            {
                if (room.GameBegin)
                {
                    // 游戏开始后，优先匹配答案
                    if (p.Message.CompareTo(room.Question) == 0)
                    {
                        var score = 0;
                        var tips = protocol.Name + "回答正确";
                        if (String.IsNullOrEmpty(room.FirstKey))
                        {
                            tips += "，加两分";
                            score = 2;
                            user.score += 2;
                            // 游戏结束倒计时
                            var ticks = DateTime.Now.Ticks;
                            // 修改倒计时为30秒(防止延迟造成倒计时已经小于30秒了又再次改成30秒的情况)
                            if (room.BeginTicks - ticks > Countdown + 5000)
                            {
                                var countdownPro = _newResponseProtocol(protocol.Key, true, protocol.Id);
                                countdownPro.Message = (Countdown / 1000).ToString();
                                countdownPro.Type = Protocol.Types.ProtocolType.Countdown;
                                countdownPro.Broadcast = true;
                                _broadcastRoomMessage(room, protocol.Key, countdownPro.ToByteArray());
                                room.Timer.Change(Countdown, -1);
                            }
                        }
                        else
                        {
                            tips += "，加一分";
                            score = 1;
                            user.score += 1;
                        }
                        p.Message = protocol.Name + "回答正确";
                        Users u = db.users.Find(protocol.Key);
                        u.score += score;
                        db.users.Update(u);
                        try
                        {
                            db.SaveChanges();
                        } catch(Exception e)
                        {
                            logger.LogError("保存用户信息出错", e);
                        }
                        // 更新用户分数
                        var updatePro = _newResponseProtocol(protocol.Key, true, protocol.Id);
                        updatePro.Type = Protocol.Types.ProtocolType.UpdateUser;
                        updatePro.Users.Add(room.GetUserInfo());
                        updatePro.Broadcast = true;
                        _broadcastRoomMessage(room, protocol.Key, updatePro.ToByteArray());
                    }
                } 
                p.Type = Protocol.Types.ProtocolType.Chat;
                p.Broadcast = true;
                _broadcastRoomMessage(room, protocol.Key, p.ToByteArray());
            } else
            {
                p.Code = -1;
                p.Error = "房间不存在";
                _sendMessage(socket, protocol.Key, p.ToByteArray());
            }
        }

        private static void _countdownTimerCallback(Object arg)
        {
            var timerObject = arg as TimerObject;
            var p = _newResponseProtocol(BroadcastEmptyKey, true, BroadcastEmptyId);
            p.Type = Protocol.Types.ProtocolType.GameEnd;
            p.Users.Add(timerObject.room.GetUserInfo());
            p.Broadcast = true;
            _broadcastRoomMessage(timerObject.room, BroadcastEmptyKey, p.ToByteArray());

            for (; timerObject.room.CurrentPaintSeat != ChatRoom.RoomCapacity;) {
                var u = timerObject.room.GetSeatUser(timerObject.room.CurrentPaintSeat++);
                if (u != null)
                {
                    timerObject.room.CurrentPaintUserKey = u.Key;
                    return;
                }
            }
            // 一轮游戏结束
            var endPro = _newResponseProtocol(BroadcastEmptyKey, true, BroadcastEmptyId);
            endPro.Type = Protocol.Types.ProtocolType.GameFinished;
            endPro.Broadcast = true;
            _broadcastRoomMessage(timerObject.room, BroadcastEmptyKey, endPro.ToByteArray());

            timerObject.room.GameBegin = false;
            timerObject.timer.Dispose();
            timerObject.room.ResetRoom();
            
        }

        /// <summary>
        /// 创建房间
        /// </summary>
        /// <param name="socket">连接的websocket</param>
        /// <param name="protocol">解析后的协议</param>
        private static void _handleCreateRoom(WebSocket socket, Protocol protocol)
        {
            var user = _getOrCreateUser(socket, protocol);
            ChatRoom room = _getRoom(protocol.RoomKey);
            _joinRoom(room, user, protocol);
        }

        /// <summary>
        /// 加入房间
        /// </summary>
        /// <param name="socket">连接的websocket</param>
        /// <param name="protocol">解析后的协议</param>
        private static void _handleJoinRoom(WebSocket socket, Protocol protocol)
        {
            User user = _getOrCreateUser(socket, protocol);
            ChatRoom room = _getRoom(protocol.RoomKey);
            _joinRoom(room, user, protocol);
        }

        /// <summary>
        /// 用户离开房间
        /// </summary>
        /// <param name="socket"></param>
        /// <param name="protocol"></param>
        private static void _handleLeaveRoom(WebSocket socket, Protocol protocol)
        {
            var p = _newResponseProtocol(protocol.Key, true, protocol.Id);
            p.Type = Protocol.Types.ProtocolType.LeavedRoom;
            User user = _getOrCreateUser(socket, protocol);
            ChatRoom room = _getRoom(protocol.RoomKey);
            if (room != null)
            {
                room.RemoveUser(user.Key);
            } 
            _sendMessage(socket, protocol.Key, p.ToByteArray());

            // 如果房间里没有用户，回收掉房间
            if (room.IsEmpty())
            {
                room.Users.Clear();
                _removeRoom(room.key);
                room = null;
            }

            _broadcastRoomInfo(null);
        }

        /// <summary>
        /// 开始游戏
        /// </summary>
        /// <param name="socket"></param>
        /// <param name="protocol"></param>
        private static void _handleGameBegin(WebSocket socket, Protocol protocol)
        {
            var p = _newResponseProtocol(protocol.Key, true, protocol.Id);
            var room = _getUserRoom(protocol.Key);
            if (room.key != protocol.RoomKey || protocol.Seat != room.CurrentPaintSeat 
                || protocol.Key != room.CurrentPaintUserKey)
            {
                p.Code = CODE_ERROR;
                p.Error = "没有权限";
                _sendMessage(socket, protocol.Key, p.ToByteArray());
                logger.LogError("开始游戏, 数据有问题", protocol.ToString(), room.ToString());
                return;
            }
            if (room.Users.Count < CanBeginReadyCounts || room.GetReadyCount() < CanBeginReadyCounts)
            {
                p.Code = CODE_ERROR;
                p.Error = "准备人数不够，不能开始游戏";
                _sendMessage(socket, protocol.Key, p.ToByteArray());
                logger.LogError("准备人数不够，不能开始游戏", protocol.ToString(), room.ToString());
                return;
            }

            room.GameBegin = true;
            // 广播房间信息给大厅用户
            _broadcastRoomInfo(null);

            // 随机获取一个
            Random random = new Random();
            var counts = db.users.FromSql("SELECT COUNT(*) FROM question").Count();
            var id = 0;
            do
            {
                id = random.Next(counts + 1);
                Thread.Sleep(10);
            }
            while (id == room.LastQuesId);

            // 提前给数据库里添加问题
            Question question;
            do
            {
                question = db.questions.Where(q => q.id == id).FirstOrDefault();
                Thread.Sleep(10);
            } while (question == null);
            room.Question = question.name;
            room.LastQuesId = id;

            p.Type = Protocol.Types.ProtocolType.GameBegin;
            p.Broadcast = true;
            _broadcastRoomMessage(room, protocol.Key, p.ToByteArray());

            // 发送要绘画的内容
            var paintPro = _newResponseProtocol(protocol.Key, true, BroadcastEmptyId);
            paintPro.Type = Protocol.Types.ProtocolType.GameBegin;
            paintPro.Message = room.Question;
            _sendMessage(socket, protocol.Key, paintPro.ToByteArray());

            // 开启倒计时
            var timerArg = new TimerObject();
            var timer = new Timer(_countdownTimerCallback, timerArg, GameTotalCountdown, -1);
            room.BeginTicks = DateTime.Now.Ticks;
            room.Timer = timer;
            timerArg.room = room;
            timerArg.timer = timer;

            logger.LogInformation("游戏开始", protocol.ToString(), room.ToString());
        }

        private static void _handlePaint(WebSocket socket, Protocol protocol)
        {
            var room = _getUserRoom(protocol.Key);
            if (room.key != protocol.RoomKey || protocol.Seat != room.CurrentPaintSeat
                || protocol.Key != room.CurrentPaintUserKey)
            {
                var p = _newResponseProtocol(protocol.Key, true, protocol.Id);
                p.Code = CODE_ERROR;
                p.Error = "没有权限";
                _sendMessage(socket, protocol.Key, p.ToByteArray());
                logger.LogError("开始游戏, 数据有问题", protocol.ToString(), room.ToString());
                return;
            }
            if (!room.GameBegin)
            {
                var p = _newResponseProtocol(protocol.Key, true, protocol.Id);
                p.Code = CODE_ERROR;
                p.Error = "游戏没有开始";
                _sendMessage(socket, protocol.Key, p.ToByteArray());
                logger.LogError("开始游戏, 数据有问题", protocol.ToString(), room.ToString());
                return;
            }
            _broadcastRoomMessage(room, protocol.Key, protocol.ToByteArray());
        }

        private static User _getOrCreateUser(WebSocket socket, Protocol protocol)
        {
            User user = _getUser(protocol.Key);
            if (user == null)
            {
                user = new User(
                    protocol.Key,
                    protocol.Name,
                    socket,
                    -1);
                Users.TryAdd(protocol.Key, user);
            }
            return user;
        }

        private static void _joinRoom(ChatRoom room, User user, Protocol protocol)
        {
            if (room != null)
            {
                // 房间已经存在
                if (room.IsFull())
                {
                    // 房间已经满了
                    var proto = _newResponseProtocol(protocol.Key, false, protocol.Id);
                    proto.Error = "房间已经满了";
                    _sendMessage(user.Socket, protocol.Key, proto.ToByteArray());
                    return;
                }
            }
            else
            {
                room = new ChatRoom(user.Key);
                room.Name = protocol.RoomName;
                room.Avatar = protocol.Avatar;
            }
            room.AddUser(user);

            var p = _newResponseProtocol(protocol.Key, true, protocol.Id);
            p.RoomKey = protocol.RoomKey;
            p.Type = Protocol.Types.ProtocolType.EnteredRoom;
            p.Users.Add(room.GetUserInfo());
            p.Broadcast = true;
            _broadcastRoomMessage(room, protocol.Key, p.ToByteArray());

            _broadcastRoomInfo(null);
        }

        private static ChatRoom _getUserRoom(string key)
        {
            lock (roomLock)
            {
                foreach (var room in Rooms)
                {
                    if (room.Value.GetUser(key) != null)
                    {
                        return room.Value;
                    }
                }
                return null;
            }
        }

        private async static void _sendMessage(WebSocket socket, string key, byte[] buf)
        {
            var buffer = new ArraySegment<byte>(buf);
            try
            {
                await socket.SendAsync(buffer, WebSocketMessageType.Binary, true, token);
            } catch (Exception e)
            {
                logger.LogError(e.Message);
                // 没有被移除的用户，进行离线处理
                if (_hasUser(key))
                {
                    _userOffline(key);
                }
            }
        }
        
        private async static void _broadcastRoomMessage(ChatRoom room, string userKey, byte[] buf)
        {
            var buffer = new ArraySegment<byte>(buf);
            var enumerator = room.Users.GetEnumerator();
            while (enumerator.MoveNext())
            {
                var user = enumerator.Current;
                if (user.Value == null || (userKey != BroadcastEmptyKey && user.Value.Key == userKey) || user.Value.Offline)
                {
                    continue;
                }
                try
                {
                    await user.Value.Socket.SendAsync(buffer, WebSocketMessageType.Binary, true, token);
                } catch (Exception e)
                {
                    logger.LogError(e.Message);
                    // 没有被移除的用户，进行离线处理
                    if (_hasUser(userKey))
                    {
                        _userOffline(userKey);
                    }
                }
            }
        }

        // 给大厅用户广播房间信息
        private async static void _broadcastRoomInfo(User user)
        {
            var p = _newResponseProtocol(BroadcastEmptyKey, true, BroadcastEmptyId);
            p.Broadcast = true;
            p.Rooms.Add(_getRoomInfo());
            var buffer = new ArraySegment<byte>(p.ToByteArray());

            if (user == null)
            {
                // 给所有在大厅的用户广播房间信息
                var enumerator = Users.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    var value = enumerator.Current;
                    if (value.Value != null && !value.Value.InRoom)
                    {
                        try
                        {
                            await value.Value.Socket.SendAsync(buffer, WebSocketMessageType.Binary, true, token);
                        }
                        catch (Exception e)
                        {
                            logger.LogError(e.Message);
                            // 没有被移除的用户，进行离线处理
                            var u = _removeUser(value.Value.Key);
                            u = null;
                        }
                    }
                }
            } else
            {
                // 给单个在大厅的用户发送房间信息
                try
                {
                    await user.Socket.SendAsync(buffer, WebSocketMessageType.Binary, true, token);
                }
                catch (Exception e)
                {
                    logger.LogError(e.Message);
                    // 没有被移除的用户，进行离线处理
                    var u = _removeUser(user.Key);
                    u = null;
                }
            }
        }

        private static void _userOffline(string key)
        {
            var room = _getUserRoom(key);
            if (room != null)
            {
                if (!room.GameBegin)
                {
                    // 如果游戏没有开始，清理用户信息
                    var user = room.RemoveUser(key);
                    user = _removeUser(key);
                    user = null;
                    // 如果房间里没有用户，回收掉房间
                    if (room.IsEmpty())
                    {
                        room.Users.Clear();
                        _removeRoom(room.key);
                        room = null;
                    }
                }
                else
                {
                    // 如果游戏已经开始，标记用户离线
                    var user = _getUser(key);
                    if (user != null)
                    {
                        user.Offline = true;
                        // 通知房间的用户，有用户离线
                        Protocol pro = _newResponseProtocol(BroadcastEmptyKey, true, BroadcastEmptyId);
                        pro.Type = Protocol.Types.ProtocolType.Offline;
                        pro.Broadcast = true;
                        _broadcastRoomMessage(room, BroadcastEmptyKey, pro.ToByteArray());
                        return;
                    }
                }
            }
        }

        private static Protocol _newResponseProtocol(string key, bool success, int id)
        {
            var p = new Protocol();
            p.Key = key;
            p.Code = success ? CODE_SUCCESS : CODE_ERROR;
            p.Id = id;
            return p;
        }

        private static User _removeUser(string key)
        {
            if (!Users.ContainsKey(key))
            {
                return null;
            }
            User user;
            while (!Users.TryRemove(key, out user))
            {
                logger.LogInformation("_removeUser -> TryRemove error");
                Thread.Sleep(1);
            }
            return user;
        }

        private static ChatRoom _removeRoom(string roomKey)
        {
            if (!Rooms.ContainsKey(roomKey))
            {
                return null;
            }
            lock (roomLock)
            {
                ChatRoom room;
                while (!Rooms.TryRemove(roomKey, out room))
                {
                    logger.LogInformation("_removeRoom -> TryRemove error");
                    Thread.Sleep(1);
                }
                return room;
            }
        }

        private static RoomInfo[] _getRoomInfo()
        {

            var rooms = Rooms.ToArray();
            RoomInfo[] infos = new RoomInfo[rooms.Length];
            for(int i = 0;i < rooms.Length;i++)
            {
                var room = rooms[i];
                var info = new RoomInfo();
                info.Avatar = room.Value.Avatar;
                info.Counts = room.Value.Users.Count;
                info.GameBegin = room.Value.GameBegin;
                info.Name = room.Value.Name;

                infos[i] = info;
            }
            return infos;
        }
    }
}