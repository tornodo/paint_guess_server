using System.Collections.Concurrent;
using System.Threading;
using guess_server.log;
using Microsoft.Extensions.Logging;

namespace guess_server.user
{
    public class ChatRoom
    {
        private readonly object beginLock = new object();
        private ILogger logger;
        // 房间最大容量
        public const int RoomCapacity = 8;
        // 房间key
        public string key { get; }
        // 房间名称
        public string Name { set; get; }
        public string Avatar { set; get; }
        // 游戏开始标记
        public bool GameBegin {
            set
            {
                lock(beginLock)
                {
                    this.GameBegin = value;
                }
            }
            get
            {
                lock(beginLock)
                {
                    return this.GameBegin;
                }
            }
        }
        // 问题（答案）
        public string Question { set; get; }
        // 上一次问题的id
        public int LastQuesId { set; get; }
        // 第一个回答正确的用户
        public string FirstKey { set; get; }
        // 游戏开始时间
        public long BeginTicks { set; get; }
        public Timer Timer { set; get; }
        // 当前哪个用户画
        public int CurrentPaintSeat { set; get; }
        public string CurrentPaintUserKey { set; get; }
        // 房间里的用户
        public ConcurrentDictionary<string, User> Users { get; }
        

        public ChatRoom(string key)
        {
            logger = Log.CeateLogger(Log.DefaultLogger);
            this.key = key;
            Users = new ConcurrentDictionary<string, User>();
        }

        public bool IsFull()
        {
            return this.Users.Count == RoomCapacity;
        }

        public bool IsEmpty()
        {
            return this.Users.Count == 0;
        }

        public RoomUser[] GetUserInfo()
        {
            RoomUser[] userInfos = new RoomUser[Users.Count];
            var enumerator = Users.GetEnumerator();
            while (enumerator.MoveNext())
            {
                var user = enumerator.Current;
                if (user.Value != null)
                {
                    RoomUser info = new RoomUser();
                    info.Avatar = user.Value.Avatar;
                    info.Name = user.Value.Name;
                    info.Seat = user.Value.Seat;
                    info.Score = user.Value.score;
                    userInfos[userInfos.Length - 1] = info;
                }
            }
            return userInfos;
        }

        public int GetReadyCount()
        {
            int counts = 0;
            var enumerator = Users.GetEnumerator();
            while (enumerator.MoveNext())
            {
                var user = enumerator.Current;
                if (user.Value != null && user.Value.Seat != -1)
                {
                    counts++;
                }
            }
            return counts;
        }

        // 游戏已经开始，不会添加和删除用户，可以直接遍历用户容器
        public User GetSeatUser(int seat)
        {
            foreach(var u in Users)
            {
                if (u.Value.Seat == seat)
                {
                    return u.Value;
                }
            }
            return null;
        }

        public bool AddUser(User user)
        {
            if (this.IsFull())
            {
                return false;
            }
            user.InRoom = true;
            return Users.TryAdd(user.Key, user);
        }

        public User GetUser(string key)
        {
            User user = null;
            if (Users.ContainsKey(key))
            {
                while(!Users.TryGetValue(key, out user))
                {
                    logger.LogInformation("ChatRoom GetUser -> TryGetValue error");
                    Thread.Sleep(1);
                }
            }
            return user;
        }

        public User RemoveUser(string key)
        {
            User user;
            while(!Users.TryRemove(key, out user))
            {
                logger.LogInformation("ChatRoom RemoveUser -> TryRemove error");
                Thread.Sleep(1);
            }
            if (user != null)
            {
                user.InRoom = false;
            }
            return user;
        }

        public void ResetRoom()
        {
            this.Question = "";
            this.FirstKey = "";
            this.BeginTicks = 0;
            this.Timer = null;
            this.CurrentPaintSeat = 0;
            this.CurrentPaintUserKey = "";
        }
        
    }
}