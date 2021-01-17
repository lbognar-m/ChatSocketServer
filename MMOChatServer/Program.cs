using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using Newtonsoft.Json;

namespace MMOChatServer
{
public enum Command
{
    Login,              //0 - Log into the server
    Logout,             //1 - Logout of the server
    Message,            //2 - Send a text message to all the chat clients
    PrivateMessage,     //3
    GroupInvite,        //4
    GroupLeave,         //5
    GroupUpdate,        //6 - is sent to the game servers
    AcceptInvite,       //7 - both for groups and clans
    DeclineInvite,      //8 - both for groups and clans
    GroupMessage,       //9
    GroupKick,          //10
       
    ClanLeave,          //11
    ClanInvite,         //12
    
    NotUsed,            //13   
    NotUsed2,           //14
    
    ClanMessage,        //15
    ClanKick,           //16

    ClanUpdate,         //17 - is sent to the game servers

    ClanCreate,         //18
    ClanDisband,        //19

    Null        //No command
}

// State object for reading client data asynchronously
public class StateObject
{
    // Client  socket.
    public Socket workSocket = null;
    // Size of receive buffer.
    public const int BufferSize = 1024;
    // Receive buffer.
    public byte[] buffer = new byte[BufferSize];
    // Received data string.
    public StringBuilder sb = new StringBuilder();
}

public class Player
{   
    public Socket socket;

    public string name;
    public int id = -1;

    public Group group;
    public int clan;
    public bool isClanLeader;
    public Player(Socket InSocket)
    {
        socket = InSocket;
    }

    public Player pendingInvite; //the player who invited this player and is waiting for a reply
    public bool hasPendingClanInvite;
}

public class Group
{
    public Group(Player player1, Player player2)
    {
        players = new List<Player>() { player1, player2 };

        player1.group = player2.group = this;

        leader = player1;
        
    }

    public List<Player> players;

    public Player leader;
    public string GetPlayerNames()
    {
        return string.Join(",", players.Select(x => x.name));
    }

}

public class AsynchronousSocketListener
{
    // Thread signal.
    public static ManualResetEvent allDone = new ManualResetEvent(false);

    public AsynchronousSocketListener()
    {
    }

    static int maxGroupSize = 5;
    static List<Player> players = new List<Player>();
    static List<Socket> gameServers = new List<Socket>();
    static List<Group> groups = new List<Group>();
      

    static Dictionary<string, System.Timers.Timer> pendingKicks = new Dictionary<string, System.Timers.Timer>();
    static Dictionary<Player, System.Timers.Timer> clearPendingInvites = new Dictionary<Player, System.Timers.Timer>(); //TD: clear this on accept/decline

    static Dictionary<string, int> CharacterIds = new Dictionary<string,int>(); // character name - character id
    static Dictionary<int, int> CharacterClans = new Dictionary<int,int>(); //character id - clain id
    static List<int> clanLeaders = new List<int>(); //ids of the characters which are clan leaders

    static Dictionary<int, string> ClanNames = new Dictionary<int,string>(); //clan id - clan name

    static string Hostname; //for php scripts, is loaded from hostname.ini
    public static void StartListening()
    {
        // Read the file as one string
        var myFile = new System.IO.StreamReader("hostname.ini");
        Hostname = myFile.ReadToEnd();

        myFile.Close();

        Console.WriteLine("url: "+ Hostname);

        UpdateClans();

        // Data buffer for incoming data.
        byte[] bytes = new Byte[1024];

        // Establish the local endpoint for the socket.

        IPEndPoint localEndPoint = new IPEndPoint(IPAddress.Any, 3457);

        // Create a TCP/IP socket.
        Socket listener = new Socket(AddressFamily.InterNetwork,
            SocketType.Stream, ProtocolType.Tcp);

        // Bind the socket to the local endpoint and listen for incoming connections.
        try
        {
            listener.Bind(localEndPoint);
            listener.Listen(100);

            while (true)
            {
                // Set the event to nonsignaled state.
                allDone.Reset();

                // Start an asynchronous socket to listen for connections.
                Console.WriteLine("Waiting for a connection...");
                listener.BeginAccept(
                    new AsyncCallback(AcceptCallback),
                    listener);

                // Wait until a connection is made before continuing.
                allDone.WaitOne();
            }

        }
        catch (Exception e)
        {
            Console.WriteLine(e.ToString());
        }

        Console.WriteLine("\nPress ENTER to continue...");
        Console.Read();

    }

    public static void AcceptCallback(IAsyncResult ar)
    {
        // Signal the main thread to continue.
        allDone.Set();

        // Get the socket that handles the client request.
        Socket listener = (Socket)ar.AsyncState;
        Socket handler = listener.EndAccept(ar);

        // Create the state object.
        StateObject state = new StateObject();
        state.workSocket = handler;
        handler.BeginReceive(state.buffer, 0, StateObject.BufferSize, 0,
            new AsyncCallback(ReadCallback), state);
    }

    public static void ReadCallback(IAsyncResult ar)
    {
        String content = String.Empty;

        // Retrieve the state object and the handler socket
        // from the asynchronous state object.
        StateObject state = (StateObject)ar.AsyncState;
        Socket handler = state.workSocket;
        List<Player> toDelete = new List<Player>(); //important so we don't have a "collection modified" error
        Player thisPlayer = players.Where(x => x.socket == handler).SingleOrDefault();
        bool isGameServer = gameServers.Contains(handler);

        string PhpResponse;
        int bytesRead;
        if (handler.Connected)
        {
            // Read data from the client socket. 

            try
            {
                bytesRead = handler.EndReceive(ar);

                if (bytesRead > 0)
                {
                    //Transform the array of bytes received from the user into an
                    //intelligent form of object Data
                    Data msgReceived = new Data(state.buffer);
                    byte[] message;
                    Player targetPlayer;
                    Data msgToSend = new Data();
                    msgToSend.cmdCommand = msgReceived.cmdCommand;
                    if (thisPlayer != null) msgToSend.strName = thisPlayer.name;
                    msgToSend.strMessage = msgReceived.strMessage;

                    switch (msgReceived.cmdCommand)
                    {
                        case Command.Login:
                            Console.WriteLine("logged in: " + msgReceived.strName);

                            if (msgReceived.strName == "i am a game server") //you can change this to some password or check the server ip here
                            {
                                gameServers.Add(handler);
                                Console.WriteLine("added a game server");

                                SendGroupUpdate(); //TD: send this update only to the new server
                            }
                            else
                            {
                                Player newPlayer = new Player(handler);
                                newPlayer.name = msgReceived.strName;

                                if (CharacterIds.ContainsKey(newPlayer.name))
                                {
                                    newPlayer.id = CharacterIds[newPlayer.name];
                                }

                                if (CharacterClans.ContainsKey(newPlayer.id))
                                {
                                    newPlayer.clan = CharacterClans[newPlayer.id];
                                }
                                if (clanLeaders.Contains(newPlayer.id))  newPlayer.isClanLeader = true;
 

                                players.RemoveAll(x => x.name == newPlayer.name);
                                players.Add(newPlayer);

                                foreach (var group in groups)
                                    for (int i = 0; i < group.players.Count; i++)
                                        if (group.players[i].name == newPlayer.name)
                                        {
                                            group.players[i] = newPlayer;
                                            newPlayer.group = group;
                                        }

                                if (pendingKicks.ContainsKey(newPlayer.name)) //cancel automatic kick
                                {
                                    pendingKicks[newPlayer.name].Stop();
                                    pendingKicks.Remove(newPlayer.name);
                                }

                                Console.WriteLine("added a player");
                            }

                            break;

                        case Command.Message:

                            if (thisPlayer == null) break;

                            Console.WriteLine(thisPlayer.name + ": " + msgReceived.strMessage);

                            message = msgToSend.ToBytes();
                            
                            foreach (Player player in players)
                            {
                                try
                                {
                                    player.socket.Send(message);
                                }
                                catch (SocketException socketException)
                                {
                                    //WSAECONNRESET, the other side closed impolitely 
                                    if (socketException.ErrorCode == 10054 ||
                                       ((socketException.ErrorCode != 10004) &&
                                       (socketException.ErrorCode != 10053)))
                                    {
                                        Console.WriteLine("receiver disconnected");
                                    }
                                    else Console.WriteLine(socketException.Message);

                                    toDelete.Add(player);
                                }
                                catch (Exception e)
                                {
                                    Console.WriteLine(e.Message + "\n" + e.StackTrace);
                                    toDelete.Add(player);
                                }
                            }

                            break;

                        case Command.PrivateMessage:

                            if (thisPlayer == null) break;

                            Console.WriteLine(thisPlayer.name + " whispers to " + msgReceived.strName + ": " + msgReceived.strMessage);

                            targetPlayer = players.Where(x => String.Compare(x.name, msgReceived.strName, true) == 0).SingleOrDefault(); //case insensitive

                            if (targetPlayer != null)
                            {
                                message = msgToSend.ToBytes();

                                targetPlayer.socket.Send(message);

                                //send the message to the sender so it appears in the log:

                                thisPlayer.socket.Send(message);
                            }

                            break;

                        case Command.GroupMessage:

                            if (thisPlayer == null || thisPlayer.group == null) break;                            

                            Console.WriteLine("[GROUP] " + thisPlayer.name + ": " + msgReceived.strMessage);
                            message = msgToSend.ToBytes();

                            foreach (Player groupMember in thisPlayer.group.players)
                                    groupMember.socket.Send(message);                            

                            break;

                        case Command.ClanMessage:

                            if (thisPlayer == null || thisPlayer.clan == 0) break;                            

                            Console.WriteLine("[CLAN] " + thisPlayer.name + ": " + msgReceived.strMessage);
                      
                            message = msgToSend.ToBytes();

                            foreach (Player clanMember in players.Where(x=>x.clan == thisPlayer.clan))
                                clanMember.socket.Send(message);                            

                            break;
                        case Command.GroupInvite:
                            if (thisPlayer == null) break;
                            if (thisPlayer.group != null && thisPlayer.group.leader != thisPlayer) break; //if the inviting player is not the group's leader
                            if (thisPlayer.group != null && thisPlayer.group.players.Count >= maxGroupSize) break; //check max group size

                            //find the player that we want to invite
                            targetPlayer = players.Where(x => x.name == msgReceived.strName).SingleOrDefault();
                            if (targetPlayer == null || targetPlayer == thisPlayer) break;

                            if (targetPlayer.group == null) //if the player is not already in a group
                            {
                                if (targetPlayer.pendingInvite != null) break; //check if there's a pending invite

                                targetPlayer.pendingInvite = thisPlayer;
                                targetPlayer.hasPendingClanInvite = false;

                                System.Timers.Timer myTimer = new System.Timers.Timer();
                                myTimer.Elapsed += (sender, args) => ClearPendingInvite(sender, targetPlayer);
                                myTimer.Interval = 20000; // 1000 ms is one second
                                myTimer.Start();

                                clearPendingInvites.Add(targetPlayer, myTimer);

                                message = msgToSend.ToBytes();

                                targetPlayer.socket.Send(message);
                            }

                            break;

                        case Command.AcceptInvite:
                            if (thisPlayer == null) break;
                            Player invitingPlayer = thisPlayer.pendingInvite;
                            if (invitingPlayer == null || invitingPlayer.clan == 0) break;

                            if (thisPlayer.hasPendingClanInvite) //clan
                            {
                                ClearInviteBeforeTick(thisPlayer);
                                
                                if (thisPlayer.clan != 0) break;

                                PhpResponse = SendPhpRequest("mmo_clan_add_character",
                               "{\"character_name\":\"" + thisPlayer.name + "\"," +
                               "\"clan_id\":\"" + invitingPlayer.clan + "\"}"
                               );

                                if (PhpResponse.Contains("OK")) UpdateClans();
                            }
                            else  //group                           
                            {
                                ClearInviteBeforeTick(thisPlayer);

                                if (thisPlayer.group != null) break;

                                bool joinedGroup = false;

                                if (invitingPlayer.group == null) //create a new group
                                {
                                    Group newGroup = new Group(invitingPlayer, thisPlayer);

                                    joinedGroup = true;

                                    groups.Add(newGroup);
                                }
                                else if (invitingPlayer.group.players.Count < maxGroupSize) //add the new player to the existing group
                                {
                                    invitingPlayer.group.players.Add(thisPlayer);
                                    thisPlayer.group = invitingPlayer.group;
                                    joinedGroup = true;
                                }

                                if (joinedGroup)
                                {
                                    SendGroupUpdate();
                                }
                            }                          

                            break;

                        case Command.ClanCreate:

                            if (thisPlayer == null) break;

                            PhpResponse = SendPhpRequest("mmo_clan_create", 
                                "{\"character_name\":\"" + thisPlayer.name + "\"," +
                                "\"clan_name\":\"" + msgReceived.strName + "\"}"
                                );

                            if (PhpResponse.Contains("OK")) UpdateClans();

                            break;

                        case Command.ClanInvite:

                            if (thisPlayer == null || thisPlayer.clan == 0 || !thisPlayer.isClanLeader) break; //check that the player is the clan leader

                            //find the player that we want to invite
                            targetPlayer = players.Where(x => x.name == msgReceived.strName).SingleOrDefault();
                            if (targetPlayer == null || targetPlayer == thisPlayer) break;

                            if (targetPlayer.clan == 0) //if the player is not already in a clan
                            {
                                if (targetPlayer.pendingInvite != null) break; //check if there's a pending invite

                                targetPlayer.pendingInvite = thisPlayer;
                                targetPlayer.hasPendingClanInvite = true;

                                System.Timers.Timer myTimer = new System.Timers.Timer();
                                myTimer.Elapsed += (sender, args) => ClearPendingInvite(sender, targetPlayer);
                                myTimer.Interval = 20000; // 1000 ms is one second
                                myTimer.Start();

                                clearPendingInvites.Add(targetPlayer, myTimer);
                                msgToSend.strMessage = ClanNames[thisPlayer.clan];
                                Console.WriteLine("clan: " + msgToSend.strMessage);
                                message = msgToSend.ToBytes();

                                targetPlayer.socket.Send(message); //forward the ClanInvite message to the target player
                            }

                            break;

                        case Command.ClanDisband:
                            if (thisPlayer == null || thisPlayer.clan == 0 || !thisPlayer.isClanLeader) break;

                            PhpResponse = SendPhpRequest("mmo_clan_disband", 
                                "{\"character_id\":\"" + thisPlayer.id + "\"}"
                                );

                            if (PhpResponse.Contains("OK")) UpdateClans();

                            break;
                        case Command.ClanLeave:
                            if (thisPlayer == null || thisPlayer.clan == 0 || thisPlayer.isClanLeader) break; //for now, the leader can't leave the clan

                            PhpResponse = SendPhpRequest("mmo_clan_remove_character",
                                "{\"character_id\":\"" + thisPlayer.id + "\"}"
                                );

                            if (PhpResponse.Contains("OK")) UpdateClans();

                            break;
                        case Command.ClanKick:
                            if (thisPlayer == null || thisPlayer.clan == 0 || !thisPlayer.isClanLeader) break;

                            //find the player that we want to invite
                            targetPlayer = players.Where(x => x.name == msgReceived.strName).SingleOrDefault();

                            if (thisPlayer.clan != targetPlayer.clan) break;

                            PhpResponse = SendPhpRequest("mmo_clan_remove_character",
                                "{\"character_id\":\"" + targetPlayer.id + "\"}"
                                );

                            if (PhpResponse.Contains("OK")) UpdateClans();

                            break;
                        case Command.DeclineInvite:
                            if (thisPlayer == null) break;
                            Player invitPlayer = thisPlayer.pendingInvite;
                            message = msgToSend.ToBytes();
                            invitPlayer.socket.Send(message);
                            ClearInviteBeforeTick(thisPlayer);

                            break;

                        case Command.GroupLeave:
                            if (thisPlayer == null) break;

                            RemoveFromGroup(thisPlayer);

                            break;

                        case Command.GroupKick:
                            if (!isGameServer && (thisPlayer == null || thisPlayer.group == null || thisPlayer.group.leader != thisPlayer)) break;

                            //find the player that we want to kick
                            targetPlayer = players.Where(x => x.name == msgReceived.strName).SingleOrDefault();

                            if (targetPlayer == null || (!isGameServer && (targetPlayer.group != thisPlayer.group))) break;

                            Console.WriteLine("received kick command: " + targetPlayer.name);

                            if (isGameServer) //kick issued by server (because of disconnect), kick after a delay of X seconds
                            {
                                if (!pendingKicks.ContainsKey(targetPlayer.name))
                                {
                                    System.Timers.Timer myTimer = new System.Timers.Timer();
                                    myTimer.Elapsed += (sender, args) => KickAfterDelay(sender, targetPlayer);
                                    myTimer.Interval = 30000; // 1000 ms is one second
                                    myTimer.Start();

                                    pendingKicks.Add(targetPlayer.name, myTimer);
                                }
                            }
                            else
                            {
                                RemoveFromGroup(targetPlayer); //kick issued by group leader, kick immediately                                                                 

                                //notify the player that they were kicked from the group:
                                message = msgToSend.ToBytes();
                                targetPlayer.socket.Send(message);
                            }

                            break;
                    }

                    // listen again:          
                    state.sb.Clear();
                    handler.BeginReceive(state.buffer, 0, StateObject.BufferSize, 0,
                    new AsyncCallback(ReadCallback), state);

                }
            }
            catch (SocketException socketException)
            {
                //WSAECONNRESET, the other side closed impolitely 
                if (socketException.ErrorCode == 10054 ||
                   ((socketException.ErrorCode != 10004) &&
                   (socketException.ErrorCode != 10053)))
                {
                    Console.WriteLine("remote client disconnected");

                }
                else Console.WriteLine(socketException.Message);
                toDelete.Add(thisPlayer);
                handler = null;
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message + "\n" + e.StackTrace);
                toDelete.Add(thisPlayer);
            }

        }

        foreach (var playerToDelete in toDelete)
        {        
            if (playerToDelete != null) RemoveFromGroup(playerToDelete);

            players.RemoveAll(x => x == playerToDelete);
        }

    }

    static string SendPhpRequest(string script, string json)
    {
        var httpWebRequest = (HttpWebRequest)WebRequest.Create(Hostname + script + ".php");
        httpWebRequest.ContentType = "application/json";
        httpWebRequest.Method = "POST";

        using (var streamWriter = new StreamWriter(httpWebRequest.GetRequestStream()))
        {
            streamWriter.Write(json);
            streamWriter.Flush();
            streamWriter.Close();
        }

        var httpResponse = (HttpWebResponse)httpWebRequest.GetResponse();
        using (var streamReader = new StreamReader(httpResponse.GetResponseStream()))
        {
            return streamReader.ReadToEnd();
        }
    }
    static void RemoveFromGroup(Player player)
    {
        if (player.group == null) return;
    
        Group group = player.group;        

        Console.WriteLine("kicking " + player.name);

        group.players.Remove(player);

        if (group.leader == player && group.players.Count > 0) group.leader = group.players[0];

        player.group = null;

        if (group.players.Count <= 1) //if there is less than 2 players now, disband the group
        {
            if (group.leader != null) group.leader.group = null;
            groups.Remove(group);
        }

        SendGroupUpdate();
    }        

    public class CharacterRow
    {        
        public int character_id { get; set; }
        public string character_name { get; set; }
        public int clan_id { get; set; }
        public string clan_name { get; set; }
        public bool is_leader { get; set; }       
    }

    public class ClansOutput
    {
        public string status { get; set; }
        public List<CharacterRow> clans { get; set; }
    }

    static void UpdateClans()
    {        
        string PhpResponse = SendPhpRequest("mmo_clan_list", "");

        Console.WriteLine(PhpResponse);

        ClansOutput result = JsonConvert.DeserializeObject<ClansOutput>(PhpResponse);

        CharacterIds.Clear();
        CharacterClans.Clear();
        clanLeaders.Clear();
        ClanNames.Clear();

        foreach (var player in players)
        {
            player.clan = 0;
        }            

        foreach (var row in result.clans)
        {
            CharacterIds.Add(row.character_name, row.character_id);
            CharacterClans.Add(row.character_id, row.clan_id);
            if (row.is_leader) clanLeaders.Add(row.character_id);

            if (!ClanNames.ContainsKey(row.clan_id))
                ClanNames.Add(row.clan_id, row.clan_name);

            Player foundPlayer = players.Where(x => x.name == row.character_name).FirstOrDefault();
            if (foundPlayer != null)
            {
                foundPlayer.id = row.character_id;
                foundPlayer.clan = row.clan_id;
                foundPlayer.isClanLeader = row.is_leader;
            }
        }

        Data msgToSend = new Data();
        msgToSend.cmdCommand = Command.ClanUpdate;
                   
        byte[] message = msgToSend.ToBytes();

        List<Socket> toDelete = new List<Socket>();

        foreach (Socket gameServer in gameServers)
        {
            try { gameServer.Send(message); }
            catch { toDelete.Add(gameServer); }
        }

        foreach (var serverToDelete in toDelete)
        gameServers.Remove(serverToDelete);
       
    }

    static void SendGroupUpdate()
    {
        Data msgToSend = new Data();
        msgToSend.cmdCommand = Command.GroupUpdate;

        try
        {
            foreach (Group group in groups) //each group is divided by ":"
            {
                msgToSend.strMessage += group.GetPlayerNames();
                msgToSend.strMessage += ":";
            }
            byte[] message = msgToSend.ToBytes();

            List<Socket> toDelete = new List<Socket>();

            foreach (Socket gameServer in gameServers)
            {
                try { gameServer.Send(message); }
                catch { toDelete.Add(gameServer); }
            }

            foreach (var serverToDelete in toDelete)
                gameServers.Remove(serverToDelete);
        }
        catch { }      
    }

    static void KickAfterDelay(object sender, Player playerToKick)
    {
        RemoveFromGroup(playerToKick);
    }

    static void ClearInviteBeforeTick(Player targetPlayer)
    {
        if (clearPendingInvites.ContainsKey(targetPlayer))
        {
            clearPendingInvites[targetPlayer].Stop();
            clearPendingInvites.Remove(targetPlayer);
        }

        Console.WriteLine("clearing pending invite before its time");

        targetPlayer.pendingInvite = null;
        targetPlayer.hasPendingClanInvite = false;
    }

    static void ClearPendingInvite(object sender, Player invitedPlayer)
    {
        ((System.Timers.Timer)sender).Enabled = false;

        Console.WriteLine("clearing pending invite");

        invitedPlayer.pendingInvite = null;
        invitedPlayer.hasPendingClanInvite = false;
    }
    private static void Send(Socket handler, String data)
    {
        // Convert the string data to byte data using ASCII encoding.
        byte[] byteData = Encoding.ASCII.GetBytes(data);

        // Begin sending the data to the remote device.
        handler.BeginSend(byteData, 0, byteData.Length, 0,
            new AsyncCallback(SendCallback), handler);
    }

    private static void SendCallback(IAsyncResult ar)
    {
        try
        {
            // Retrieve the socket from the state object.
            Socket handler = (Socket)ar.AsyncState;

            // Complete sending the data to the remote device.
            int bytesSent = handler.EndSend(ar);
            Console.WriteLine("Sent {0} bytes to client.", bytesSent);

        }
        catch (Exception e)
        {
            Console.WriteLine(e.ToString());
        }
    }


    public static int Main(String[] args)
    {
        StartListening();
        return 0;
    }

}

}