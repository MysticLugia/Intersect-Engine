﻿#define websockets
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Intersect.Localization;
using Intersect.Logging;
using Intersect.Network;
using Intersect.Network.Crypto;
using Intersect.Network.Crypto.Formats;
using Intersect.Server.Classes.Core;
using Intersect.Server.Classes.General;
using Intersect.Server.Classes.Networking;
using Intersect.Server.Network;
using Open.Nat;
using Intersect.Utilities;

namespace Intersect.Server.Classes
{
    using Database = Intersect.Server.Classes.Core.Database;

    public class ServerStart
    {
        private static bool _errorHalt = true;
        public static ServerNetwork SocketServer;

        public static void Start(string[] args)
        {
            if (RunningOnWindows()) SetConsoleCtrlHandler(new HandlerRoutine(ConsoleCtrlCheck), true);
            Console.CancelKeyPress += Console_CancelKeyPress;

            //Place sqlite3.dll where it's needed.
            var dllname = Environment.Is64BitProcess ? "sqlite3x64.dll" : "sqlite3x86.dll";
            if (!ReflectionUtils.ExtractResource($"Intersect.Server.Resources.{dllname}", "sqlite3.dll"))
            {
                Log.Error("Failed to extract sqlite library, terminating startup.");
                Environment.Exit(-0x1000);
            }


            Thread logicThread;
            if (!ServerOptions.LoadOptions())
            {
                Console.WriteLine("Failed to load server options! Press any key to shut down.");
                Console.ReadKey();
                return;
            }

            foreach (var arg in args)
            {
                if (arg.Contains("port="))
                {
                    ushort port = Options.ServerPort;
                    if (ushort.TryParse(arg.Split("=".ToCharArray())[1],out port))
                    {
                        Options.ServerPort = port;
                    }
                }
            }

            Strings.Init(Strings.IntersectComponent.Server, Options.Language);
            Console.WriteLine(@"  _____       _                          _   ");
            Console.WriteLine(@" |_   _|     | |                        | |  ");
            Console.WriteLine(@"   | |  _ __ | |_ ___ _ __ ___  ___  ___| |_ ");
            Console.WriteLine(@"   | | | '_ \| __/ _ \ '__/ __|/ _ \/ __| __|");
            Console.WriteLine(@"  _| |_| | | | ||  __/ |  \__ \  __/ (__| |_ ");
            Console.WriteLine(@" |_____|_| |_|\__\___|_|  |___/\___|\___|\__|");
            Console.WriteLine(Strings.Get("intro", "tagline"));
            Console.WriteLine("Copyright (C) 2017  Ascension Game Dev");
            Console.WriteLine(Strings.Get("intro", "version", Assembly.GetExecutingAssembly().GetName().Version));
            Console.WriteLine(Strings.Get("intro", "support"));
            Console.WriteLine(Strings.Get("intro", "loading"));
            Database.CheckDirectories();
            if (!Formulas.LoadFormulas())
            {
                Console.WriteLine(Strings.Get("formulas", "loadfailed"));
                Console.ReadKey();
                return;
            }
            if (!Database.InitDatabase())
            {
                Console.ReadKey();
                return;
            }
            CustomColors.Load();
            Console.WriteLine(Strings.Get("commandoutput", "playercount", Database.GetRegisteredPlayers()));
            Console.WriteLine(Strings.Get("commandoutput", "gametime", ServerTime.GetTime().ToString("F")));
            ServerTime.Update();
            Log.Global.AddOutput(new ConsoleOutput(Debugger.IsAttached ? LogLevel.All : LogLevel.Error));
            var assembly = Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream("Intersect.Server.private-intersect.bek"))
            {
                var rsaKey = EncryptionKey.FromStream<RsaKey>(stream);
                Debug.Assert(rsaKey != null, "rsaKey != null");
                SocketServer = new ServerNetwork(new NetworkConfiguration(Options.ServerPort), rsaKey.Parameters);
            }

            var packetHander = new PacketHandler();
            SocketServer.Handlers[PacketCode.BinaryPacket] = packetHander.HandlePacket;

#if websockets
            WebSocketNetwork.Init(Options.ServerPort);
            Console.WriteLine(Strings.Get("intro", "websocketstarted", Options.ServerPort));
#endif

            if (!SocketServer.Listen())
            {
                Log.Error("An error occurred while attempting to connect.");
            }

            Console.WriteLine();
            UPnP.ConnectNatDevice().Wait(5000);
            UPnP.OpenServerPort(Options.ServerPort, Protocol.Tcp).Wait(5000);
            UPnP.OpenServerPort(Options.ServerPort, Protocol.Udp).Wait(5000);

            Console.WriteLine();

            //Check to see if AGD can see this server. If so let the owner know :)
            var externalIp = "";
            var serverAccessible = PortChecker.CanYouSeeMe(Options.ServerPort, out externalIp);

            Console.WriteLine(Strings.Get("portchecking", "connectioninfo"));
            if (!String.IsNullOrEmpty(externalIp))
            {
                Console.WriteLine(Strings.Get("portchecking", "publicip"), externalIp);
                Console.WriteLine(Strings.Get("portchecking", "publicport"), Options.ServerPort);

                Console.WriteLine();
                if (serverAccessible)
                {
                    Console.WriteLine(Strings.Get("portchecking", "accessible"));
                    Console.WriteLine(Strings.Get("portchecking", "letothersjoin"));
                }
                else
                {
                    Console.WriteLine(Strings.Get("portchecking", "notaccessible"));
                    Console.WriteLine(Strings.Get("portchecking", "debuggingsteps"));
                    Console.WriteLine(Strings.Get("portchecking", "checkfirewalls"));
                    Console.WriteLine(Strings.Get("portchecking", "checkantivirus"));
                    Console.WriteLine(Strings.Get("portchecking", "screwed"));
                    Console.WriteLine();
                    if (!UPnP.ForwardingSucceeded())
                        Console.WriteLine(Strings.Get("portchecking", "checkrouterupnp"));
                }
            }
            else
            {
                Console.WriteLine(Strings.Get("portchecking", "notconnected"));
            }
            Console.WriteLine();
            Console.WriteLine(Strings.Get("intro", "started", Options.ServerPort));

            logicThread = new Thread(() => ServerLoop.RunServerLoop());
            logicThread.Start();
            if (args.Contains("nohalt"))
            {
                _errorHalt = false;
            }
            if (!args.Contains("noconsole"))
            {
                Console.WriteLine(Strings.Get("intro", "consoleactive"));
                Console.Write("> ");
                string command = Console.ReadLine();
                while (true)
                {
                    bool userFound = false;
                    string ip = "";
                    if (command == null)
                    {
                        ShutDown();
                        return;
                    }
                    command = command.Trim();
                    string[] commandsplit = command.Split(' ');

                    if (commandsplit[0] == Strings.Get("commands", "announcement")) //Announcement Command
                    {
                        if (commandsplit.Length > 1)
                        {
                            if (commandsplit[1] == Strings.Get("commands", "commandinfo"))
                            {
                                Console.WriteLine(@"    " +
                                                  Strings.Get("commands", "announcementusage",
                                                      Strings.Get("commands", "commandinfo")));
                                Console.WriteLine(@"    " + Strings.Get("commands", "announcementdesc"));
                            }
                            else
                            {
                                PacketSender.SendGlobalMsg(command.Remove(0, 12));
                            }
                        }
                        else
                        {
                            Console.WriteLine(Strings.Get("commandoutput", "invalidparameters",
                                Strings.Get("commands", "commandinfo")));
                        }
                    }
                    else if (commandsplit[0] == Strings.Get("commands", "onlinelist")) //Online List Command
                    {
                        Console.WriteLine(string.Format("{0,-10}", Strings.Get("commandoutput", "listid")) +
                                          string.Format("{0,-28}", Strings.Get("commandoutput", "listaccount")) +
                                          string.Format("{0,-28}", Strings.Get("commandoutput", "listcharacter")));
                        Console.WriteLine(new string('-', 66));
                        for (int i = 0; i < Globals.Clients.Count; i++)
                        {
                            if (Globals.Clients[i] != null)
                            {
                                var name = Globals.Clients[i].Entity != null
                                    ? Globals.Clients[i].Entity.MyName
                                    : "";
                                Console.WriteLine(string.Format("{0,-10}", "#" + i) +
                                                  string.Format("{0,-28}", Globals.Clients[i].MyAccount) +
                                                  string.Format("{0,-28}", name));
                            }
                        }
                    }
                    else if (commandsplit[0] == Strings.Get("commands", "kill")) //Kill Command
                    {
                        if (commandsplit.Length > 1)
                        {
                            if (commandsplit[1] == Strings.Get("commands", "commandinfo"))
                            {
                                Console.WriteLine(@"    " +
                                                  Strings.Get("commands", "killusage",
                                                      Strings.Get("commands", "commandinfo")));
                                Console.WriteLine(@"    " + Strings.Get("commands", "killdesc"));
                            }
                            else
                            {
                                for (int i = 0; i < Globals.Clients.Count; i++)
                                {
                                    if (Globals.Clients[i] != null && Globals.Clients[i].Entity != null)
                                    {
                                        string user = Globals.Clients[i].Entity.MyName.ToLower();
                                        if (user == commandsplit[1].ToLower())
                                        {
                                            Globals.Clients[i].Entity.Die();
                                            PacketSender.SendGlobalMsg(@"    " +
                                                                       Strings.Get("player", "serverkilled",
                                                                           Globals.Clients[i].Entity.MyName));
                                            Console.WriteLine(@"    " +
                                                              Strings.Get("commandoutput", "killsuccess",
                                                                  Globals.Clients[i].Entity.MyName));
                                            userFound = true;
                                            break;
                                        }
                                    }
                                }
                                if (userFound == false)
                                {
                                    Console.WriteLine(@"    " + Strings.Get("player", "offline"));
                                }
                            }
                        }
                        else
                        {
                            Console.WriteLine(Strings.Get("commandoutput", "invalidparameters",
                                Strings.Get("commands", "commandinfo")));
                        }
                    }
                    else if (commandsplit[0] == Strings.Get("commands", "kick")) //Kick Command
                    {
                        if (commandsplit.Length > 1)
                        {
                            if (commandsplit[1] == Strings.Get("commands", "commandinfo"))
                            {
                                Console.WriteLine(@"    " +
                                                  Strings.Get("commands", "kickusage",
                                                      Strings.Get("commands", "commandinfo")));
                                Console.WriteLine(@"    " + Strings.Get("commands", "kickdesc"));
                            }
                            else
                            {
                                for (int i = 0; i < Globals.Clients.Count; i++)
                                {
                                    if (Globals.Clients[i] != null && Globals.Clients[i].Entity != null)
                                    {
                                        string user = Globals.Clients[i].Entity.MyName.ToLower();
                                        if (user == commandsplit[1].ToLower())
                                        {
                                            PacketSender.SendGlobalMsg(Strings.Get("player", "serverkicked",
                                                Globals.Clients[i].Entity.MyName));
                                            Console.WriteLine(@"    " +
                                                              Strings.Get("player", "serverkicked",
                                                                  Globals.Clients[i].Entity.MyName));
                                            Globals.Clients[i].Disconnect(); //Kick em'
                                            userFound = true;
                                            break;
                                        }
                                    }
                                }
                                if (userFound == false)
                                {
                                    Console.WriteLine(@"    " + Strings.Get("player", "offline"));
                                }
                            }
                        }
                        else
                        {
                            Console.WriteLine(Strings.Get("commandoutput", "invalidparameters",
                                Strings.Get("commands", "commandinfo")));
                        }
                    }
                    else if (commandsplit[0] == Strings.Get("commands", "unban")) //Unban Command
                    {
                        if (commandsplit.Length > 1)
                        {
                            if (commandsplit[1] == Strings.Get("commands", "commandinfo"))
                            {
                                Console.WriteLine(@"    " +
                                                  Strings.Get("commands", "unbanusage",
                                                      Strings.Get("commands", "commandinfo")));
                                Console.WriteLine(@"    " + Strings.Get("commands", "unbandesc"));
                            }
                            else
                            {
                                if (Database.AccountExists(commandsplit[1]))
                                {
                                    Database.DeleteBan(commandsplit[1]);
                                    Console.WriteLine(
                                        @"    " + Strings.Get("account", "unbanned", commandsplit[1]));
                                }
                                else
                                {
                                    Console.WriteLine("    " + Strings.Get("account", "notfound", commandsplit[1]));
                                }
                            }
                        }
                        else
                        {
                            Console.WriteLine(Strings.Get("commandoutput", "invalidparameters",
                                Strings.Get("commands", "commandinfo")));
                        }
                    }
                    else if (commandsplit[0] == Strings.Get("commands", "ban")) //Ban Command
                    {
                        if (commandsplit.Length > 1)
                        {
                            if (commandsplit[1] == Strings.Get("commands", "commandinfo"))
                            {
                                Console.WriteLine(@"    " +
                                                  Strings.Get("commands", "banusage",
                                                      Strings.Get("commands", "true"),
                                                      Strings.Get("commands", "false"),
                                                      Strings.Get("commands", "commandinfo")));
                                Console.WriteLine(@"    " + Strings.Get("commands", "bandesc"));
                            }
                            else
                            {
                                if (commandsplit.Length > 3)
                                {
                                    for (int i = 0; i < Globals.Clients.Count; i++)
                                    {
                                        if (Globals.Clients[i] != null && Globals.Clients[i].Entity != null)
                                        {
                                            string user = Globals.Clients[i].Entity.MyName.ToLower();
                                            if (user == commandsplit[1].ToLower())
                                            {
                                                string reason = "";
                                                for (int n = 4; n < commandsplit.Length; n++)
                                                {
                                                    reason += commandsplit[n] + " ";
                                                }
                                                if (commandsplit[3] == Strings.Get("commands", "true"))
                                                {
                                                    ip = Globals.Clients[i].GetIP();
                                                }
                                                Database.AddBan(Globals.Clients[i],
                                                    Convert.ToInt32(commandsplit[2]),
                                                    reason,
                                                    Strings.Get("commands", "banuser"), ip);
                                                PacketSender.SendGlobalMsg(Strings.Get("account", "banned",
                                                    Globals.Clients[i].Entity.MyName));
                                                Console.WriteLine(@"    " +
                                                                  Strings.Get("account", "banned",
                                                                      Globals.Clients[i].Entity.MyName));
                                                Globals.Clients[i].Disconnect(); //Kick em'
                                                userFound = true;
                                                break;
                                            }
                                        }
                                    }
                                    if (userFound == false)
                                    {
                                        Console.WriteLine(@"    " + Strings.Get("player", "offline"));
                                    }
                                }
                                else
                                {
                                    Console.WriteLine(Strings.Get("commandoutput", "invalidparameters",
                                        Strings.Get("commands", "commandinfo")));
                                }
                            }
                        }
                        else
                        {
                            Console.WriteLine(Strings.Get("commandoutput", "invalidparameters",
                                Strings.Get("commands", "commandinfo")));
                        }
                    }
                    else if (commandsplit[0] == Strings.Get("commands", "unmute")) //Unmute Command
                    {
                        if (commandsplit.Length > 1)
                        {
                            if (commandsplit[1] == Strings.Get("commands", "commandinfo"))
                            {
                                Console.WriteLine(@"    " +
                                                  Strings.Get("commands", "unmuteusage",
                                                      Strings.Get("commands", "commandinfo")));
                                Console.WriteLine(@"    " + Strings.Get("commands", "unmutedesc"));
                            }
                            else
                            {
                                for (int i = 0; i < Globals.Clients.Count; i++)
                                {
                                    if (Globals.Clients[i] != null && Globals.Clients[i].Entity != null)
                                    {
                                        string user = Globals.Clients[i].Entity.MyName.ToLower();
                                        if (user == commandsplit[1].ToLower())
                                        {
                                            Database.DeleteMute(Globals.Clients[i].MyAccount);
                                            Globals.Clients[i].Muted = false;
                                            Globals.Clients[i].MuteReason = "";
                                            PacketSender.SendGlobalMsg(Strings.Get("account", "unmuted",
                                                Globals.Clients[i].Entity.MyName));
                                            Console.WriteLine(@"    " +
                                                              Strings.Get("account", "unmuted",
                                                                  Globals.Clients[i].Entity.MyName));
                                            userFound = true;
                                            break;
                                        }
                                    }
                                }
                                if (userFound == false)
                                {
                                    Console.WriteLine(@"    " + Strings.Get("player", "offline"));
                                }
                            }
                        }
                        else
                        {
                            Console.WriteLine(Strings.Get("commandoutput", "invalidparameters",
                                Strings.Get("commands", "commandinfo")));
                        }
                    }
                    else if (commandsplit[0] == Strings.Get("commands", "mute")) //Mute Command
                    {
                        if (commandsplit.Length > 1)
                        {
                            if (commandsplit[1] == Strings.Get("commands", "commandinfo"))
                            {
                                Console.WriteLine(@"    " +
                                                  Strings.Get("commands", "muteusage",
                                                      Strings.Get("commands", "true"),
                                                      Strings.Get("commands", "false"),
                                                      Strings.Get("commands", "commandinfo")));
                                Console.WriteLine(@"    " + Strings.Get("commands", "mutedesc"));
                            }
                            else
                            {
                                if (commandsplit.Length > 3)
                                {
                                    for (int i = 0; i < Globals.Clients.Count; i++)
                                    {
                                        if (Globals.Clients[i] != null && Globals.Clients[i].Entity != null)
                                        {
                                            string user = Globals.Clients[i].Entity.MyName.ToLower();
                                            if (user == commandsplit[1].ToLower())
                                            {
                                                string reason = "";
                                                for (int n = 4; n < commandsplit.Length; n++)
                                                {
                                                    reason += commandsplit[n] + " ";
                                                }
                                                if (commandsplit[3] == Strings.Get("commands", "true"))
                                                {
                                                    ip = Globals.Clients[i].GetIP();
                                                }
                                                Database.AddMute(Globals.Clients[i],
                                                    Convert.ToInt32(commandsplit[2]),
                                                    reason, Strings.Get("commands", "muteuser"), ip);
                                                Globals.Clients[i].Muted = true; //Cut out their tongues!
                                                Globals.Clients[i].MuteReason =
                                                    Database.CheckMute(Globals.Clients[i].MyAccount,
                                                        Globals.Clients[i].GetIP());
                                                PacketSender.SendGlobalMsg(Strings.Get("account", "muted",
                                                    Globals.Clients[i].Entity.MyName));
                                                Console.WriteLine(@"    " +
                                                                  Strings.Get("account", "muted",
                                                                      Globals.Clients[i].Entity.MyName));
                                                userFound = true;
                                                break;
                                            }
                                        }
                                    }
                                    if (userFound == false)
                                    {
                                        Console.WriteLine(@"    " + Strings.Get("player", "offline"));
                                    }
                                }
                                else
                                {
                                    Console.WriteLine(Strings.Get("commandoutput", "invalidparameters",
                                        Strings.Get("commands", "commandinfo")));
                                }
                            }
                        }
                        else
                        {
                            Console.WriteLine(Strings.Get("commandoutput", "invalidparameters",
                                Strings.Get("commands", "commandinfo")));
                        }
                    }
                    else if (commandsplit[0] == Strings.Get("commands", "power")) //Power Command
                    {
                        if (commandsplit.Length > 1)
                        {
                            if (commandsplit[1] == Strings.Get("commands", "commandinfo"))
                            {
                                Console.WriteLine(@"    " +
                                                  Strings.Get("commands", "powerusage",
                                                      Strings.Get("commands", "commandinfo")));
                                Console.WriteLine(@"    " + Strings.Get("commands", "powerdesc"));
                            }
                            else
                            {
                                if (commandsplit.Length > 2)
                                {
                                    for (int i = 0; i < Globals.Clients.Count; i++)
                                    {
                                        if (Globals.Clients[i] != null && Globals.Clients[i].Entity != null)
                                        {
                                            string user = Globals.Clients[i].Entity.MyName.ToLower();
                                            if (user == commandsplit[1].ToLower())
                                            {
                                                Database.SetPlayerPower(Globals.Clients[i].MyAccount,
                                                    int.Parse(commandsplit[2]));
                                                PacketSender.SendEntityDataToProximity(Globals.Clients[i].Entity);
                                                if (Globals.Clients[i].Power > 0)
                                                {
                                                    PacketSender.SendGlobalMsg(Strings.Get("player", "admin",
                                                        Globals.Clients[i].Entity.MyName));
                                                }
                                                else
                                                {
                                                    PacketSender.SendGlobalMsg(Strings.Get("player", "deadmin",
                                                        Globals.Clients[i].Entity.MyName));
                                                }
                                                Console.WriteLine(@"    " +
                                                                  Strings.Get("commandoutput", "powerchanged",
                                                                      Globals.Clients[i].Entity.MyName));

                                                userFound = true;
                                                break;
                                            }
                                        }
                                    }
                                    if (userFound == false)
                                    {
                                        Console.WriteLine(@"    " + Strings.Get("player", "offline"));
                                    }
                                }
                                else
                                {
                                    Console.WriteLine(Strings.Get("commandoutput", "invalidparameters",
                                        Strings.Get("commands", "commandinfo")));
                                }
                            }
                        }
                        else
                        {
                            Console.WriteLine(Strings.Get("commandoutput", "invalidparameters",
                                Strings.Get("commands", "commandinfo")));
                        }
                    }
                    else if (commandsplit[0] == Strings.Get("commands", "poweracc")) //Power Account Command
                    {
                        if (commandsplit.Length > 1)
                        {
                            if (commandsplit[1] == Strings.Get("commands", "commandinfo"))
                            {
                                Console.WriteLine(@"    " +
                                                  Strings.Get("commands", "poweraccusage",
                                                      Strings.Get("commands", "commandinfo")));
                                Console.WriteLine(@"    " + Strings.Get("commands", "poweraccdesc"));
                            }
                            else
                            {
                                if (commandsplit.Length > 2)
                                {
                                    if (commandsplit.Length > 2)
                                    {
                                        try
                                        {
                                            if (Database.AccountExists(commandsplit[1]))
                                            {
                                                Database.SetPlayerPower(commandsplit[1],
                                                    int.Parse(commandsplit[2]));
                                                Console.WriteLine(@"    " +
                                                                  Strings.Get("commandoutput", "powerchanged",
                                                                      commandsplit[1]));
                                            }
                                            else
                                            {
                                                Console.WriteLine(@"    " +
                                                                  Strings.Get("account", "notfound",
                                                                      commandsplit[1]));
                                            }
                                        }
                                        catch (Exception)
                                        {
                                            Console.WriteLine(@"    " +
                                                              Strings.Get("commandoutput", "parseerror",
                                                                  commandsplit[0],
                                                                  Strings.Get("commands", "commandinfo")));
                                        }
                                    }
                                    else
                                    {
                                        Console.WriteLine(@"    " +
                                                          Strings.Get("commandoutput", "syntaxerror",
                                                              commandsplit[0],
                                                              Strings.Get("commands", "commandinfo")));
                                    }
                                }
                                else
                                {
                                    Console.WriteLine(Strings.Get("commandoutput", "invalidparameters",
                                        Strings.Get("commands", "commandinfo")));
                                }
                            }
                        }
                        else
                        {
                            Console.WriteLine(Strings.Get("commandoutput", "invalidparameters",
                                Strings.Get("commands", "commandinfo")));
                        }
                    }
                    else if (commandsplit[0] == Strings.Get("commands", "cps")) //CPS Command
                    {
                        if (commandsplit.Length > 1)
                        {
                            if (commandsplit[1] == Strings.Get("commands", "commandinfo"))
                            {
                                Console.WriteLine(@"    " +
                                                  Strings.Get("commands", "cpsusage",
                                                      Strings.Get("commands", "commandinfo")));
                                Console.WriteLine(@"    " + Strings.Get("commands", "cpsdesc"));
                            }
                            else if (commandsplit[1] == Strings.Get("commands", "cpslock"))
                            {
                                Globals.CPSLock = true;
                            }
                            else if (commandsplit[1] == Strings.Get("commands", "cpsunlock"))
                            {
                                Globals.CPSLock = false;
                            }
                            else if (commandsplit[1] == Strings.Get("commands", "cpsstatus"))
                            {
                                if (Globals.CPSLock)
                                {
                                    Console.WriteLine(Strings.Get("commandoutput", "cpslocked"));
                                }
                                else
                                {
                                    Console.WriteLine(Strings.Get("commandoutput", "cpsunlocked"));
                                }
                            }
                            else
                            {
                                Console.WriteLine(Strings.Get("commandoutput", "invalidparameters",
                                    Strings.Get("commands", "commandinfo")));
                            }
                        }
                        else
                        {
                            Console.WriteLine(Strings.Get("commandoutput", "cps", Globals.CPS));
                        }
                    }
                    else if (commandsplit[0] == Strings.Get("commands", "exit")) //Exit Command
                    {
                        if (commandsplit.Length > 1)
                        {
                            if (commandsplit[1] == Strings.Get("commands", "commandinfo"))
                            {
                                Console.WriteLine(@"    " +
                                                  Strings.Get("commands", "exitusage",
                                                      Strings.Get("commands", "commandinfo")));
                                Console.WriteLine(@"    " + Strings.Get("commands", "exitdesc"));
                            }
                            else
                            {
                                Console.WriteLine(Strings.Get("commandoutput", "invalidparameters",
                                    Strings.Get("commands", "commandinfo")));
                            }
                        }
                        else
                        {
                            ShutDown();
                            return;
                        }
                    }
                    else if (commandsplit[0] == Strings.Get("commands", "help")) //Help Command
                    {
                        if (commandsplit.Length > 1)
                        {
                            if (commandsplit[1] == Strings.Get("commands", "commandinfo"))
                            {
                                Console.WriteLine(@"    " +
                                                  Strings.Get("commands", "helpusage",
                                                      Strings.Get("commands", "commandinfo")));
                                Console.WriteLine(@"    " + Strings.Get("commands", "helpdesc"));
                            }
                            else
                            {
                                Console.WriteLine(Strings.Get("commandoutput", "invalidparameters",
                                    Strings.Get("commands", "commandinfo")));
                            }
                        }
                        else
                        {
                            Console.WriteLine(@"    " + Strings.Get("commandoutput", "helpheader"));
                            Console.WriteLine(@"    " + string.Format("{0,-20}", Strings.Get("commands", "help")) +
                                              " - " + Strings.Get("commands", "helphelp"));
                            Console.WriteLine(@"    " + string.Format("{0,-20}", Strings.Get("commands", "exit")) +
                                              " - " + Strings.Get("commands", "exithelp"));
                            Console.WriteLine(@"    " +
                                              string.Format("{0,-20}", Strings.Get("commands", "announcement")) +
                                              " - " +
                                              Strings.Get("commands", "announcementhelp"));
                            Console.WriteLine(@"    " + string.Format("{0,-20}", Strings.Get("commands", "cps")) +
                                              " - " +
                                              Strings.Get("commands", "cpshelp"));
                            Console.WriteLine(@"    " + string.Format("{0,-20}", Strings.Get("commands", "power")) +
                                              " - " + Strings.Get("commands", "powerhelp"));
                            Console.WriteLine(
                                @"    " + string.Format("{0,-20}", Strings.Get("commands", "poweracc")) +
                                " - " + Strings.Get("commands", "poweracchelp"));
                            Console.WriteLine(
                                @"    " + string.Format("{0,-20}", Strings.Get("commands", "onlinelist")) +
                                " - " + Strings.Get("commands", "onlinelisthelp"));
                            Console.WriteLine(@"    " + string.Format("{0,-20}", Strings.Get("commands", "kick")) +
                                              " - " + Strings.Get("commands", "kickhelp"));
                            Console.WriteLine(@"    " + string.Format("{0,-20}", Strings.Get("commands", "ban")) +
                                              " - " +
                                              Strings.Get("commands", "banhelp"));
                            Console.WriteLine(@"    " + string.Format("{0,-20}", Strings.Get("commands", "unban")) +
                                              " - " + Strings.Get("commands", "unbanhelp"));
                            Console.WriteLine(@"    " + string.Format("{0,-20}", Strings.Get("commands", "mute")) +
                                              " - " + Strings.Get("commands", "mutehelp"));
                            Console.WriteLine(
                                @"    " + string.Format("{0,-20}", Strings.Get("commands", "unmute")) +
                                " - " + Strings.Get("commands", "unmutehelp"));
                            Console.WriteLine(@"    " + string.Format("{0,-20}", Strings.Get("commands", "kill")) +
                                              " - " + Strings.Get("commands", "killhelp"));
                            Console.WriteLine(@"    " +
                                              Strings.Get("commandoutput", "helpfooter",
                                                  Strings.Get("commands", "commandinfo")));
                        }
                    }
                    else
                    {
                        Console.WriteLine(@"    " + Strings.Get("commandoutput", "notfound"));
                    }
                    Console.Write("> ");
                    command = Console.ReadLine();
                }
            }
        }

        private static void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            ShutDown();
            e.Cancel = true;
        }

        private static void ShutDown()
        {
            //Save all online players
            if (Globals.Clients != null)
            {
                for (int i = 0; i < Globals.Clients.Count; i++)
                {
                    if (Globals.Clients[i] != null && Globals.Clients[i].Entity != null)
                    {
                        Database.SaveCharacter(Globals.Clients[i].Entity);
                    }
                }
            }

            Globals.ServerStarted = false;
            if (SocketServer != null) SocketServer.Dispose();
        }

        private static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            // Ignore missing resources
            if (args.Name.Contains(".resources"))
                return null;

            // check for assemblies already loaded
            Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.FullName == args.Name);
            if (assembly != null)
                return assembly;

            string filename = args.Name.Split(',')[0] + ".dll".ToLower();

            //Try Loading from libs/server first
            var libsFolder = Path.Combine(AppDomain.CurrentDomain.SetupInformation.ApplicationBase, "libs", "server");
            if (File.Exists(Path.Combine(libsFolder, filename)))
            {
                return Assembly.LoadFile(Path.Combine(libsFolder, filename));
            }
            else
            {
                string archSpecificPath = Path.Combine(AppDomain.CurrentDomain.SetupInformation.ApplicationBase,
                    Environment.Is64BitProcess
                        ? Path.Combine("libs", "server", "x64")
                        : Path.Combine("libs", "server", "x86"),
                    filename);
                if (File.Exists(archSpecificPath))
                {
                    return Assembly.LoadFile(archSpecificPath);
                }
                else
                {
                    return null;
                }
            }
        }

        //Really basic error handler for debugging purposes
        public static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Log.Error((Exception)e.ExceptionObject);
            if (e.IsTerminating)
            {
                if (_errorHalt)
                {
                    Console.WriteLine(Strings.Get("errors", "errorservercrash"));
                    Console.ReadKey();
                }
                else
                {
                    Console.WriteLine(Strings.Get("errors", "errorservercrashnohalt"));
                }
                Environment.Exit(-1);
            }
            else
            {
                Console.WriteLine(Strings.Get("errors", "errorlogged"));
            }
        }

        private static bool RunningOnWindows()
        {
            OperatingSystem os = Environment.OSVersion;
            PlatformID pid = os.Platform;
            switch (pid)
            {
                case PlatformID.Win32NT:
                case PlatformID.Win32S:
                case PlatformID.Win32Windows:
                case PlatformID.WinCE:
                    return true;
            }
            return false;
        }

        private static bool ConsoleCtrlCheck(CtrlTypes ctrlType)
        {
            // Put your own handler here
            switch (ctrlType)
            {
                case CtrlTypes.CTRL_C_EVENT:
                    //Handled Elsewhere
                    break;

                case CtrlTypes.CTRL_BREAK_EVENT:
                    //Handled Elsewhere
                    break;

                case CtrlTypes.CTRL_CLOSE_EVENT:
                    ShutDown();
                    break;

                case CtrlTypes.CTRL_LOGOFF_EVENT:
                case CtrlTypes.CTRL_SHUTDOWN_EVENT:
                    ShutDown();
                    break;

            }
            return true;
        }

        #region unmanaged
        // Declare the SetConsoleCtrlHandler function
        // as external and receiving a delegate.

        [DllImport("Kernel32")]
        public static extern bool SetConsoleCtrlHandler(HandlerRoutine Handler, bool Add);

        // A delegate type to be used as the handler routine
        // for SetConsoleCtrlHandler.
        public delegate bool HandlerRoutine(CtrlTypes CtrlType);

        // An enumerated type for the control messages
        // sent to the handler routine.
        public enum CtrlTypes
        {
            CTRL_C_EVENT = 0,
            CTRL_BREAK_EVENT,
            CTRL_CLOSE_EVENT,
            CTRL_LOGOFF_EVENT = 5,
            CTRL_SHUTDOWN_EVENT
        }

        #endregion
    }
}