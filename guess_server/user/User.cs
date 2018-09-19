using System.Net.WebSockets;

namespace guess_server.user
{
    public class User
    {
        private readonly object _offlineLock = new object();
        private readonly object _inRoomLock = new object();
        public User(string key, string name, WebSocket socket): this(key, name, socket, -1)
        {
        }
        public User(string key, string name, WebSocket socket, int seat)
        {
            this.Key = key;
            this.Name = name;
            this.Socket = socket;
            this.Seat = seat;
            this._offline = false;
            this._inRoom = false;
        }
        public string Key { get; }
        public string Name { get; }
        public int Seat { set; get; }
        public string Avatar { set; get; }
        public WebSocket Socket { set; get;}
        
        private bool _offline;
        public void SetOffline(bool value)
        {
            lock(_offlineLock)
            {
                _offline = value;
            }
        }
        public bool GetOffline()
        {
            lock(_offlineLock)
            {
                return _offline;
            }
        }

        private bool _inRoom;

        public void SetInRoom(bool value)
        {
            lock(_inRoomLock)
            {
                _inRoom = value;
            }
        }
        public bool GetInRoom()
        {
            lock(_inRoomLock)
            {
                return _inRoom;
            }
        }
        
        public int Score { set; get; }
    }
}