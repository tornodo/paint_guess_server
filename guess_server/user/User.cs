using System.Net.WebSockets;

namespace guess_server.user
{
    public class User
    {
        private readonly object offlineLock = new object();
        private readonly object inRoomLock = new object();
        public User(string key, string name, WebSocket socket): this(key, name, socket, -1)
        {
        }
        public User(string key, string name, WebSocket socket, int seat)
        {
            this.Key = key;
            this.Name = name;
            this.Socket = socket;
            this.Seat = seat;
            //this.Offline = false;
        }
        public string Key { get; }
        public string Name { get; }
        public int Seat { set; get; }
        public string Avatar { set; get; }
        public WebSocket Socket { set; get;}
        public bool Offline {
            set
            {
                lock(offlineLock)
                {
                    Offline = value;
                }
            }
            get
            {
                lock(offlineLock)
                {
                    return Offline;
                }
            }
        }
        public bool InRoom
        {
            set
            {
                lock(inRoomLock)
                {
                    InRoom = value;
                }
            }
            get
            {
                lock(inRoomLock)
                {
                    return InRoom;
                }
            }
        }
        public int score { set; get; }
    }
}