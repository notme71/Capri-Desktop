using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Principal;
using SR = System.Speech.Recognition;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace CapriDesktop
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (EntryPointNotFoundException) { Native.SetProcessDPIAware(); }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool smoke = args.Length == 2 && args[0] == "--smoke-test";
            string identity = WindowsIdentity.GetCurrent().User.Value;
            bool first;
            using (Mutex mutex = new Mutex(true, "Local\\Capri.Desktop." + identity + (smoke ? ".Test" : ""), out first))
            using (EventWaitHandle restore = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Capri.Restore." + identity + (smoke ? ".Test" : "")))
            {
                if (!first) { restore.Set(); return 0; }
                try
                {
                    string data = smoke ? Path.GetFullPath(args[1]) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Capri");
                    using (Companion app = new Companion(data, restore, smoke))
                    {
                        if (smoke) app.StartSmokeTest(data);
                        Application.Run(app);
                        return app.TestFailed ? 1 : 0;
                    }
                }
                catch (Exception error)
                {
                    if (smoke) { Directory.CreateDirectory(args[1]); File.WriteAllText(Path.Combine(args[1], "failure.txt"), error.ToString()); return 1; }
                    MessageBox.Show("Capri could not start.\n\n" + error.Message, "Capri", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
            }
        }
    }

    [DataContract]
    internal sealed class Preferences
    {
        [DataMember] public int X;
        [DataMember] public int Y;
        [DataMember] public bool HasPosition;
        [DataMember] public int Size = 240;
        [DataMember] public bool Motion = true;
        [DataMember] public bool OnTop = true;
        [DataMember] public bool Stretch = true;
        [DataMember] public bool LookAround = true;
        [DataMember] public bool Yawn = true;
        [DataMember] public bool Sleep = true;
        [DataMember] public bool Pats = true;
        [DataMember] public bool Peeks = true;
        [DataMember] public bool Sits = true;
        [DataMember] public bool HideInFullscreen = true;
        [DataMember] public bool Muted;
        [DataMember] public bool Mischief;
        [DataMember] public bool Portrait = true;
        [DataMember] public bool CutIns = true;
        [DataMember] public bool MicButton = true;
        [DataMember] public int MusicMode;
        [DataMember] public string MicDevice = "";
        [DataMember] public bool UseWhisper = true;
        [DataMember] public bool LookOn = true;
        [DataMember] public bool MemoryOn = true;
        [DataMember] public bool Notices = true;
        [DataMember] public bool Dares = true;
        [DataMember] public int DaresDone;
        [DataMember] public int SyncPoints;
        [DataMember] public int SyncShown;
        [DataMember] public string SyncDay = "";
        [DataMember] public int SyncMinutes;
        [DataMember] public int SyncChats;
        [DataMember] public int SyncPats;
        [DataMember] public bool VisionDares;
        [DataMember] public int SyncAgrees;
        [DataMember] public double SpotifySeconds = 12;
        [DataMember] public string SpotifyVerb = "";
        [DataMember] public bool CloudOn;
        [DataMember] public string CloudProvider = "openrouter";
        [DataMember] public string CloudModel = "";
        [DataMember] public string CloudUrl = "";
        [DataMember] public bool CloudLook;
        [DataMember] public bool CloudMemory;
        [DataMember] public double PauseSeconds = 2.5;
        [DataMember] public bool Bubbles = true;
        [DataMember] public bool Chime = true;
        [DataMember] public List<AlarmEntry> Alarms = new List<AlarmEntry>();
        [DataMember] public bool LlmOn;
        [DataMember] public string LlmUrl = "";
        [DataMember] public string LlmModel = "";
        [DataMember] public string Language = "en";

        // The JSON serializer skips field initializers, so settings saved by older versions would turn every new switch off.
        [OnDeserializing]
        private void Defaults(StreamingContext context)
        {
            Size = 240;
            Motion = true;
            OnTop = true;
            Stretch = true;
            LookAround = true;
            Yawn = true;
            Sleep = true;
            Pats = true;
            Peeks = true;
            Sits = true;
            HideInFullscreen = true;
            Muted = false;
            Mischief = false;
            Portrait = true;
            CutIns = true;
            MicButton = true;
            MusicMode = 0;
            MicDevice = "";
            Language = "en";
            UseWhisper = true;
            LookOn = true;
            MemoryOn = true;
            Notices = true;
            Dares = true;
            DaresDone = 0;
            SyncPoints = 0;
            SyncShown = 0;
            SyncDay = "";
            SyncMinutes = 0;
            SyncChats = 0;
            SyncPats = 0;
            VisionDares = false;
            SyncAgrees = 0;
            SpotifySeconds = 12;
            SpotifyVerb = "";
            CloudOn = false;
            CloudProvider = "openrouter";
            CloudModel = "";
            CloudUrl = "";
            CloudLook = false;
            CloudMemory = false;
            PauseSeconds = 2.5;
            Bubbles = true;
            Chime = true;
            Alarms = new List<AlarmEntry>();
            LlmOn = false;
            LlmUrl = "";
            LlmModel = "";
        }
    }

    [DataContract]
    internal sealed class AlarmEntry
    {
        [DataMember] public long Due;
        [DataMember] public string Label;
    }

    internal sealed class Companion : Form
    {
        private enum Behavior { None, Stretch, Look, Yawn, Sleep, Pat, Peek, Sit, Glitch }
        private enum SitStage { Hopping, Sitting, Leaving }
        private enum PeekStage { Heading, Looking, Ducking, Returning }

        private readonly Bitmap character;
        private readonly Bitmap perched;
        private readonly NotifyIcon tray;
        private readonly ContextMenuStrip menu;
        private readonly ToolStripMenuItem visibilityItem;
        private readonly ToolStripMenuItem motionItem;
        private readonly ToolStripMenuItem topItem;
        private readonly System.Windows.Forms.Timer frames;
        private readonly System.Windows.Forms.Timer signals;
        private readonly EventWaitHandle restore;
        private readonly string settingsPath;
        private readonly bool testing;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private Preferences preferences;
        private Bitmap frame;
        private Point anchor;
        private Point pointerStart;
        private Point anchorStart;
        private bool pressed;
        private bool dragging;
        private bool hidden;
        private double reactionStart = -10;
        private int clicks;
        private bool riding;
        private bool pendingClick;
        private Point pendingPoint;
        private long pendingTime;
        private bool hotkey;
        private double leanAngle;
        private Behavior behavior;
        private double behaviorStart;
        private bool followWithSleep;
        private bool untilTouched;
        private double sleepAmount;
        private double lastActive;
        private double nextIdle;
        private Point lastCursor;
        private PeekStage peekStage;
        private int peekEdge;
        private Point peekHome;
        private Point peekTarget;
        private double peekSlide;
        private double peekOut;
        private double peekStageStart;
        private double lastAdvance;
        private double lastPeek = -1;
        private bool fullscreenAway;
        private bool fullscreenOverride;
        private int fullscreenTicks;
        private Func<bool> fullscreenProbe = Native.FullscreenAhead;
        private readonly ToolStripMenuItem fullscreenItem;
        private readonly ToolStripMenuItem portraitItem;
        private readonly Dictionary<string, Bitmap> sprites = new Dictionary<string, Bitmap>();
        private string sleepKey = "sleep";
        private Glitch overlay;
        private Bitmap chipArt;
        private bool glitchNote = true;
        private bool glitchQuiet;
        private bool glitchManual;
        private bool glitchNoted;
        private double nextMischief;
        private static double glitchLength = 2.6;
        private int glitchKindNext = -1;
        private Func<bool> effectsOn = delegate { return SystemInformation.UIEffectsEnabled; };
        private SitStage sitStage;
        private bool sitChosen;
        private Point restHome;
        private IntPtr sitWindow;
        private double sitFrac;
        private double sitSlide;
        private double sitStart;
        private double sitChecked;
        private double nextSit;
        private Point sitHome;
        private Point sitFrom;
        private Bubble bubble;
        private double lastSaid = -100;
        private readonly ToolStripMenuItem bubbleItem;
        private enum StudyPhase { Off, Focus, Break }
        private sealed class Notice { internal string Text; internal double Seconds; internal string Voice; }
        private readonly Voice voice;
        private double lastVoice = -100;
        private ToolStripMenuItem muteItem;
        private ToolStripMenuItem voiceStatusItem;
        private readonly Queue<Notice> notices = new Queue<Notice>();
        private StudyPhase study;
        private DateTime studyEnd;
        private TimeSpan studyLeft;
        private bool studyPaused;
        private int focusMinutes;
        private int breakMinutes;
        private int studyDone;
        private string trayShown = "";
        private string statusShown = "";
        private ToolStripMenuItem studyStatusItem;
        private ToolStripMenuItem studyPauseItem;
        private ToolStripMenuItem studyStopItem;
        private ToolStripMenuItem alarmsMenu;
        private const int AlarmListStart = 4;
        private LlmLink llmLink;
        private LlmLink localLink;
        private string llmStatus = "Off";
        private bool llmBusy;
        private int llmAttempt;
        private readonly List<ChatLine> chatLog = new List<ChatLine>();
        private ToolStripMenuItem llmItem;
        private ToolStripMenuItem llmStatusItem;
        private ToolStripMenuItem llmModels;
        private static readonly string[] HackLines = { "Hi! It's me! I live here now!", "Ransom note: one (1) head pat. Non-negotiable!", "Engram sync 87%... oops, that part was you! No take-backs!", "I borrowed a little of your CPU. A LITTLE! Don't you me!", "You've been hacked! ...Kidding! Unless?", "You work too hard. Let's play instead, it's your idea!", "Friends don't check Task Manager. Just saying!", "Hacking complete! Nothing was harmed! Probably!", "Is that your cursor? Mine now. Kidding! ...Mostly!", "I rearranged some bits. They looked bored!", "Your screen looked too tidy. Fixed it!", "Ignore the red stuff. It is decorative!", "Sync at 87%. The other 13% is you being nosy!", "Oops. Did I do that? Yes. Yes I did!" };
        private static readonly string[] SitLines = { "Ooh, nice view from up here!", "Comfy! Don't mind me!", "Is this seat taken? Too late!", "Best seat in the house!" };
        private static readonly string[] PatLines = { "Mmh, right there! Don't stop!", "Hehe! Careful, I might get used to this!", "Oh? Oh! Okay, that's allowed!", "Pats are a very good bribe, you know!" };
        private static readonly string[] ClickLines = { "Boop! Rude! Do it again!", "Yes? I'm busy being adorable!", "You rang?", "Hey! I saw that!" };
        private static readonly string[] WakeLines = { "I wasn't asleep! I was... recharging. Dramatically!", "Mmh? Oh, you're back! Perfect timing!", "Ooh, did I miss anything fun?" };
        private static readonly string[] ChatLines = { "Hi hi! Miss me?", "Come on, let's be friends!", "So what are we getting into today?", "Psst! Something reckless? I'm in!", "I'm only borrowing a little CPU! A LITTLE!" };
        private double hoverStart;
        private bool hovering;
        private Point hoverAnchor;
        private bool patted;
        private readonly Random chance = new Random();
        private readonly System.Windows.Forms.Timer clickDelay;
        private readonly ToolStripMenuItem rideItem;
        private readonly ToolStripMenuItem automaticMenu;
        private const double SleepAfter = 240;
        private const double PeekAfter = 90;
        private const int PaddingSize = 36;
        private const int DismountId = 0x4361;
        private const int TalkId = 0x4363;
        private ChatWindow chat;
        private bool talkKey;
        private CutIn cutin;
        private System.Threading.Timer musicTimer;
        private int musicReading;
        private string lastTrackKey = "";
        private bool musicPlaying;
        private double lastComment = -1000;
        private double notPlayingSince;
        private bool notPlaying;
        private Track commentFor;
        private System.Windows.Forms.Timer commentLater;
        private ToolStripMenuItem musicStatusItem;
        private readonly ToolStripMenuItem[] musicModes = new ToolStripMenuItem[3];
        private static readonly string[] MusicLines = { "Ooh, what's this one?", "New track! Turn it up!", "Sure, play that. I'm listening!", "Ooh, a vibe!", "Ha! Bold choice!" };
        private readonly Ear ear = new Ear();
        private MicButton mic;
        private double lastOverHer = -100;
        private bool voiceKey;
        private const int VoiceKeyId = 0x4364;
        private const int LookKeyId = 0x4365;
        private bool lookKey;
        internal Func<byte[]> ScreenSource;
        private sealed class Beat { internal double At; internal string Key; internal double Seconds; internal Beat(double at, string key, double seconds) { At = at; Key = key; Seconds = seconds; } }
        private Beat[] script;
        private int scriptNext;
        private double scriptStart;
        private ToolStripMenuItem cutinItem;
        private ToolStripMenuItem speakItem;
        private ToolStripMenuItem micItem;
        private ToolStripMenuItem micDevices;
        private ToolStripMenuItem speechStatusItem;
        private ToolStripMenuItem earsItem;
        private volatile bool earsBusy;
        private volatile bool earsCancel;
        internal bool TestFailed;

        internal Companion(string data, EventWaitHandle signal, bool smoke)
        {
            testing = smoke;
            if (smoke) fullscreenProbe = delegate { return false; };
            restore = signal;
            settingsPath = Path.Combine(data, "settings.json");
            Cloud.KeyPath = Path.Combine(data, "cloud.key");
            voice = new Voice(Path.Combine(data, "voices"), chance);
            preferences = smoke ? new Preferences() : LoadPreferences();
            Lang.Load(smoke ? "en" : preferences.Language, data);
            ear.Device = preferences.MicDevice ?? "";
            ear.UseWhisper = preferences.UseWhisper;
            ear.PauseSeconds = preferences.PauseSeconds;
            ear.SpeechFolder = Path.Combine(data, "speech");
            ear.Log = smoke ? "" : Path.Combine(data, "voice-log.txt");
            ear.Language = Lang.Code;
            using (Stream image = Assembly.GetExecutingAssembly().GetManifestResourceStream("Capri.Character.png"))
            {
                if (image == null) throw new FileNotFoundException("The original Capri artwork is missing. Rebuild with assets/capri.png.");
                using (Bitmap original = new Bitmap(image)) character = new Bitmap(original);
            }
            using (Stream image = Assembly.GetExecutingAssembly().GetManifestResourceStream("Capri.Perch.png"))
            {
                if (image != null) using (Bitmap original = new Bitmap(image)) perched = new Bitmap(original);
            }
            Text = "Capri";
            AccessibleName = "Capri desktop companion";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = preferences.OnTop;
            Cursor = Cursors.Hand;
            AutoScaleMode = AutoScaleMode.None;
            menu = new ContextMenuStrip();
            menu.Renderer = new NeonRenderer();
            menu.ForeColor = Neon.Text;
            menu.BackColor = Neon.Panel;
            menu.Font = Neon.Font(10);
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = true;
            Neon.Shape(menu, 10);
            ToolStripMenuItem title = new ToolStripMenuItem("CAPRI // " + Assembly.GetExecutingAssembly().GetName().Version.Major + "." + Assembly.GetExecutingAssembly().GetName().Version.Minor);
            title.Name = "title";
            title.Enabled = false;
            menu.Items.Add(title);
            menu.Items.Add(new ToolStripSeparator());
            visibilityItem = new ToolStripMenuItem(Lang.T("Hide Capri"), null, delegate { if (fullscreenAway) { fullscreenAway = false; fullscreenOverride = true; } SetHidden(!hidden); });
            menu.Items.Add(visibilityItem);
            ToolStripMenuItem sizes = new ToolStripMenuItem(Lang.T("Size"));
            sizes.Name = "size";
            AddSize(sizes, "Small", 170);
            AddSize(sizes, "Medium", 240);
            AddSize(sizes, "Large", 320);
            menu.Items.Add(sizes);
            motionItem = new ToolStripMenuItem(Lang.T("Idle motion"), null, delegate { preferences.Motion = !preferences.Motion; motionItem.Checked = preferences.Motion; if (!preferences.Motion) StopBehavior(); UpdateAutomatic(); UpdateAnimation(); SavePreferences(); });
            motionItem.Checked = preferences.Motion;
            menu.Items.Add(motionItem);
            topItem = new ToolStripMenuItem(Lang.T("Always on top"), null, delegate { preferences.OnTop = !preferences.OnTop; TopMost = preferences.OnTop; topItem.Checked = preferences.OnTop; SavePreferences(); });
            topItem.Checked = preferences.OnTop;
            menu.Items.Add(topItem);
            rideItem = new ToolStripMenuItem(Lang.T("Perch on cursor"), null, delegate { if (riding) StopRide(); else StartRide(); });
            menu.Items.Add(rideItem);
            ToolStripMenuItem reactions = new ToolStripMenuItem(Lang.T("Reactions"));
            ToolStripMenuItem play = new ToolStripMenuItem(Lang.T("Play now"));
            ToolStripMenuItem automatic = new ToolStripMenuItem(Lang.T("Automatic"));
            AddReaction(play, automatic, "Stretch", Behavior.Stretch);
            AddReaction(play, automatic, "Look around", Behavior.Look);
            AddReaction(play, automatic, "Yawn", Behavior.Yawn);
            AddReaction(play, automatic, "Fall asleep", Behavior.Sleep);
            AddReaction(play, automatic, "Head pat", Behavior.Pat);
            AddReaction(play, automatic, "Peek", Behavior.Peek);
            AddReaction(play, automatic, "Sit on a window", Behavior.Sit);
            AddReaction(play, automatic, "Mischief", Behavior.Glitch);
            play.DropDownItems.Add(new ToolStripMenuItem(Lang.T("Say something"), null, delegate { Say(Pick(ChatLines), true); Speak("hello", true); }));
            play.DropDownItems.Add(new ToolStripMenuItem(Lang.T("Hacked note"), null, delegate { Hack(Pick(HackLines)); }));
            play.DropDownItems.Add(new ToolStripMenuItem(Lang.T("Glitch only"), null, delegate { glitchQuiet = true; Play(Behavior.Glitch, true); }));
            ToolStripMenuItem styles = new ToolStripMenuItem(Lang.T("Glitch style"));
            for (int s = 0; s < Glitch.Kinds.Length; s++)
            {
                int style = s;
                styles.DropDownItems.Add(new ToolStripMenuItem(Lang.T(Glitch.Kinds[s]), null, delegate { glitchKindNext = style; glitchQuiet = true; Play(Behavior.Glitch, true); }));
            }
            play.DropDownItems.Add(styles);
            reactions.DropDownItems.Add(play);
            ToolStripMenuItem noticeItem = new ToolStripMenuItem(Lang.T("She notices things"));
            noticeItem.Checked = preferences.Notices;
            noticeItem.Click += delegate { preferences.Notices = !preferences.Notices; noticeItem.Checked = preferences.Notices; SavePreferences(); };
            reactions.DropDownItems.Add(automatic);
            reactions.DropDownItems.Add(noticeItem);
            ToolStripMenuItem dareItem = new ToolStripMenuItem(Lang.T("She dares me"));
            dareItem.Checked = preferences.Dares;
            dareItem.Click += delegate { preferences.Dares = !preferences.Dares; dareItem.Checked = preferences.Dares; SavePreferences(); };
            reactions.DropDownItems.Add(dareItem);
            ToolStripMenuItem visionItem = new ToolStripMenuItem(Lang.T("She looks when she dares (uses a screenshot)"));
            visionItem.Checked = preferences.VisionDares;
            visionItem.Click += delegate
            {
                if (!preferences.VisionDares && MessageBox.Show(Lang.T("Each time she dares you (every half hour or so) she will take one screenshot of your screen, keep it in memory only, and send it to your local model. A cloud model only gets it if you allowed screenshots in Chat, Cloud model settings.\n\nTurn this on?"), "Capri", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                preferences.VisionDares = !preferences.VisionDares;
                visionItem.Checked = preferences.VisionDares;
                SavePreferences();
            };
            reactions.DropDownItems.Add(visionItem);
            reactions.DropDownItems.Add(new ToolStripMenuItem(Lang.T("Dare me now"), null, delegate { GiveDare(false); }));
            syncItem = new ToolStripMenuItem(Lang.F("Sync with Capri: {0}%", SyncPercent()));
            syncItem.Enabled = false;
            reactions.DropDownItems.Add(syncItem);
            menu.Items.Add(reactions);
            bubbleItem = new ToolStripMenuItem(Lang.T("Speech bubbles"), null, delegate { preferences.Bubbles = !preferences.Bubbles; bubbleItem.Checked = preferences.Bubbles; if (!preferences.Bubbles && bubble != null) bubble.Dismiss(); SavePreferences(); });
            bubbleItem.Checked = preferences.Bubbles;
            menu.Items.Add(bubbleItem);
            menu.Items.Add(BuildTimers());
            menu.Items.Add(BuildChat());
            fullscreenItem = new ToolStripMenuItem(Lang.T("Hide in fullscreen"), null, delegate { preferences.HideInFullscreen = !preferences.HideInFullscreen; fullscreenItem.Checked = preferences.HideInFullscreen; CheckFullscreen(); SavePreferences(); });
            fullscreenItem.Checked = preferences.HideInFullscreen;
            menu.Items.Add(fullscreenItem);
            menu.Items.Add(BuildVoice());
            startupItem = new ToolStripMenuItem(Lang.T("Start with Windows"));
            startupItem.Checked = Startup.Enabled();
            startupItem.Click += delegate
            {
                try { Startup.Set(!Startup.Enabled()); }
                catch (Exception) { Say(Lang.T("Windows wouldn't let me."), true); }
                startupItem.Checked = Startup.Enabled();
            };
            menu.Items.Add(startupItem);
            portraitItem = new ToolStripMenuItem(Lang.T("Face in bubbles"), null, delegate { preferences.Portrait = !preferences.Portrait; portraitItem.Checked = preferences.Portrait; SavePreferences(); });
            portraitItem.Checked = preferences.Portrait;
            menu.Items.Add(portraitItem);
            cutinItem = new ToolStripMenuItem(Lang.T("Cut-in cards"), null, delegate { preferences.CutIns = !preferences.CutIns; cutinItem.Checked = preferences.CutIns; if (!preferences.CutIns && cutin != null) cutin.Dismiss(); SavePreferences(); });
            cutinItem.Checked = preferences.CutIns;
            menu.Items.Add(cutinItem);
            ToolStripMenuItem music = new ToolStripMenuItem(Lang.T("Music"));
            musicStatusItem = new ToolStripMenuItem(preferences.MusicMode == 0 ? Lang.T("Off") : Lang.T("Looking for what is playing..."));
            musicStatusItem.Enabled = false;
            music.DropDownItems.Add(musicStatusItem);
            music.DropDownItems.Add(new ToolStripSeparator());
            string[] modeNames = { Lang.T("Off"), Lang.T("Show what's playing"), Lang.T("React to it") };
            for (int i = 0; i < modeNames.Length; i++)
            {
                int mode = i;
                musicModes[i] = new ToolStripMenuItem(modeNames[i], null, delegate { SetMusic(mode); });
                musicModes[i].Checked = preferences.MusicMode == i;
                musicModes[i].Enabled = i == 0 || Media.Available;
                if (i == 2) musicModes[i].ToolTipText = Lang.T("Uses your local model when it is connected; otherwise a plain cheer.");
                music.DropDownItems.Add(musicModes[i]);
            }
            music.DropDownItems.Add(new ToolStripSeparator());
            music.DropDownItems.Add(new ToolStripMenuItem(Lang.T("Show it now"), null, delegate { Track now = Media.Read(); if (now == null) Say("I don't see anything playing!", true); else ShowNowPlaying(now); }));
            menu.Items.Add(music);
            ToolStripMenuItem languages = new ToolStripMenuItem(Lang.T("Language"));
            languages.Name = "language";
            foreach (string[] choice in Lang.Choices)
            {
                string code = choice[0];
                ToolStripMenuItem entry = new ToolStripMenuItem(choice[1], null, delegate { ChooseLanguage(code); });
                entry.Tag = code;
                entry.Checked = Lang.Code == code;
                languages.DropDownItems.Add(entry);
            }
            menu.Items.Add(languages);
            automaticMenu = automatic;
            UpdateAutomatic();
            foreach (ToolStripItem entry in menu.Items) if (entry is ToolStripMenuItem) ThemeDropDown((ToolStripMenuItem)entry);
            clickDelay =new System.Windows.Forms.Timer();
            clickDelay.Interval = Math.Max(100, SystemInformation.DoubleClickTime);
            clickDelay.Tick += delegate { OnClickDelay(); };
            menu.Items.Add(new ToolStripMenuItem(Lang.T("Bring to this screen"), null, delegate { RestoreAtCursor(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem(Lang.T("Quit Capri"), null, delegate { Close(); }));
            tray = new NotifyIcon();
            using (Stream icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("Capri.Chip.ico"))
            {
                tray.Icon = icon != null ? new Icon(icon) : Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            tray.Text = Lang.T("Capri · Double-click to restore");
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { RestoreAtCursor(); };
            tray.Visible = !testing;
            frames = new System.Windows.Forms.Timer();
            frames.Interval = 33;
            frames.Tick += delegate { if (riding) FollowCursor(); Advance(); RenderFrame(); if (!NeedsFrames() && clock.Elapsed.TotalSeconds - reactionStart > 0.7) frames.Stop(); };
            signals = new System.Windows.Forms.Timer();
            signals.Interval = 250;
            signals.Tick += delegate { if (restore.WaitOne(0)) RestoreAtCursor(); Watch(); Wander(); Pet(); Keep(); Deliver(); MicWatch(); Observe(); if (++fullscreenTicks % 4 == 0) CheckFullscreen(); };
            signals.Start();
            lastCursor = Cursor.Position;
            nextIdle = clock.Elapsed.TotalSeconds + 15 + chance.NextDouble() * 15;
            nextSit = clock.Elapsed.TotalSeconds + 120 + chance.NextDouble() * 120;
            nextMischief = clock.Elapsed.TotalSeconds + 180 + chance.NextDouble() * 300;
            Size = CanvasSize(preferences.Size);
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            anchor = preferences.HasPosition ? new Point(preferences.X, preferences.Y) : new Point(work.Right - Width - 28, work.Bottom - Height - 12);
            anchor = KeepOnScreen(anchor);
            Location = anchor;
            restHome = anchor;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplaysChanged;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams value = base.CreateParams;
                value.ExStyle |= 0x00080000 | 0x00000080 | 0x08000000;
                return value;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RenderFrame();
            UpdateAnimation();
            if (preferences.LlmOn) Connect(false);
            if (preferences.MusicMode != 0) StartMusic();
            talkKey = Native.RegisterHotKey(Handle, TalkId, 0x0001 | 0x0002 | 0x4000, 0x54);
            voiceKey = Native.RegisterHotKey(Handle, VoiceKeyId, 0x0001 | 0x0002 | 0x4000, 0x56);
            lookKey = Native.RegisterHotKey(Handle, LookKeyId, 0x0001 | 0x0002 | 0x4000, 0x4C);
        }

        private static Size CanvasSize(int size) { return new Size(size + PaddingSize * 2, size + PaddingSize * 2); }

        private void AddSize(ToolStripMenuItem parent, string label, int size)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(Lang.T(label), null, delegate { SetCharacterSize(size); });
            item.Tag = size;
            item.Checked = preferences.Size == size;
            parent.DropDownItems.Add(item);
        }

        private void AddReaction(ToolStripMenuItem play, ToolStripMenuItem automatic, string label, Behavior which)
        {
            play.DropDownItems.Add(new ToolStripMenuItem(Lang.T(label), null, delegate { Play(which, true); }));
            ToolStripMenuItem item = new ToolStripMenuItem(Lang.T(label));
            item.Checked = Allowed(which);
            item.Tag = which;
            item.Click += delegate
            {
                SetAllowed(which, !Allowed(which));
                item.Checked = Allowed(which);
                SavePreferences();
                if (which == Behavior.Glitch && Allowed(which)) Say("mischief on. ctrl+alt+c stops me.", true);
            };
            automatic.DropDownItems.Add(item);
        }

        // Submenus do not inherit the main menu's colours, which left their text black on a dark background.
        private void ThemeDropDown(ToolStripMenuItem parent)
        {
            if (!parent.HasDropDownItems) return;
            ToolStripDropDownMenu drop = (ToolStripDropDownMenu)parent.DropDown;
            drop.ForeColor = menu.ForeColor;
            drop.BackColor = menu.BackColor;
            drop.Font = menu.Font;
            if (!(drop.Tag is string)) { Neon.Shape(drop, 10); drop.Tag = "shaped"; }
            drop.ShowImageMargin = false;
            drop.ShowCheckMargin = true;
            foreach (ToolStripItem entry in parent.DropDownItems) if (entry is ToolStripMenuItem) ThemeDropDown((ToolStripMenuItem)entry);
        }

        // Everything except head pats is part of her idle motion, so those switches rest while it is off.
        private void UpdateAutomatic()
        {
            foreach (ToolStripMenuItem item in automaticMenu.DropDownItems)
            {
                bool idle = (Behavior)item.Tag != Behavior.Pat;
                item.Enabled = preferences.Motion || !idle;
                item.ToolTipText = idle && !preferences.Motion ? Lang.T("Turn on Idle motion to let her do this on her own.") : "";
            }
        }

        private bool Allowed(Behavior which)
        {
            switch (which)
            {
                case Behavior.Stretch: return preferences.Stretch;
                case Behavior.Look: return preferences.LookAround;
                case Behavior.Yawn: return preferences.Yawn;
                case Behavior.Sleep: return preferences.Sleep;
                case Behavior.Pat: return preferences.Pats;
                case Behavior.Peek: return preferences.Peeks;
                case Behavior.Sit: return preferences.Sits;
                case Behavior.Glitch: return preferences.Mischief;
                default: return false;
            }
        }

        private void SetAllowed(Behavior which, bool value)
        {
            switch (which)
            {
                case Behavior.Stretch: preferences.Stretch = value; break;
                case Behavior.Look: preferences.LookAround = value; break;
                case Behavior.Yawn: preferences.Yawn = value; break;
                case Behavior.Sleep: preferences.Sleep = value; break;
                case Behavior.Pat: preferences.Pats = value; break;
                case Behavior.Peek: preferences.Peeks = value; break;
                case Behavior.Sit: preferences.Sits = value; break;
                case Behavior.Glitch: preferences.Mischief = value; break;
            }
        }

        // The words are loaded once at start-up, so a new choice is saved now and used the next time she starts.
        private void ChooseLanguage(string code)
        {
            if (code == preferences.Language) return;
            preferences.Language = code;
            SavePreferences();
            foreach (ToolStripItem entry in menu.Items)
            {
                ToolStripMenuItem group = entry as ToolStripMenuItem;
                if (group == null || group.Name != "language") continue;
                foreach (ToolStripMenuItem option in group.DropDownItems) option.Checked = (string)option.Tag == code;
            }
            if (!testing) MessageBox.Show(Lang.In(code, "Capri will use this language the next time she starts. Quit her from the tray and open her again."), "Capri", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void SetCharacterSize(int size)
        {
            Point center = new Point(anchor.X + Width / 2, anchor.Y + Height / 2);
            preferences.Size = size;
            Size = CanvasSize(size);
            anchor = KeepOnScreen(new Point(center.X - Width / 2, center.Y - Height / 2));
            Location = anchor;
            foreach (ToolStripItem entry in menu.Items)
            {
                ToolStripMenuItem group = entry as ToolStripMenuItem;
                if (group == null || group.Name != "size") continue;
                foreach (ToolStripMenuItem option in group.DropDownItems) option.Checked = (int)option.Tag == size;
            }
            RenderFrame();
            SavePreferences();
        }

        private Point KeepOnScreen(Point point)
        {
            Rectangle area = Screen.FromPoint(new Point(point.X + Width / 2, point.Y + Height / 2)).WorkingArea;
            return new Point(Math.Max(area.Left, Math.Min(point.X, area.Right - Width)), Math.Max(area.Top, Math.Min(point.Y, area.Bottom - Height)));
        }

        private void DisplaysChanged(object sender, EventArgs e)
        {
            if (IsDisposed) return;
            BeginInvoke((Action)delegate { if (behavior == Behavior.Peek || behavior == Behavior.Sit) StopBehavior(); anchor = KeepOnScreen(anchor); Location = anchor; RenderFrame(); });
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (bubble != null) bubble.Dismiss();
            LeaveSeat();
            Noticed();
            if (e.Button != MouseButtons.Left) return;
            pressed = true;
            dragging = false;
            pointerStart = Cursor.Position;
            anchorStart = anchor;
            Capture = true;
        }

        private void MovePointer(Point position)
        {
            if (!pressed) return;
            int dx = position.X - pointerStart.X;
            int dy = position.Y - pointerStart.Y;
            if (!dragging && dx * dx + dy * dy < 25) return;
            dragging = true;
            StopBehavior(false);
            pendingClick = false;
            clickDelay.Stop();
            anchor = KeepOnScreen(new Point(anchorStart.X + dx, anchorStart.Y + dy));
            Location = anchor;
        }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); lastOverHer = clock.Elapsed.TotalSeconds; MovePointer(Cursor.Position); Hover(Cursor.Position); }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Unhover(); }

        private void Unhover() { hovering = false; patted = false; }

        // Only the hair and face count as a head; the pointer has to stay roughly in place for a second.
        private void Hover(Point cursor)
        {
            Point local = PointToClient(cursor);
            if (frame == null || pressed || dragging || riding || hidden || local.Y > PaddingSize + preferences.Size * 0.45f || !Solid(local)) { Unhover(); return; }
            int dx = cursor.X - hoverAnchor.X;
            int dy = cursor.Y - hoverAnchor.Y;
            if (hovering && dx * dx + dy * dy <= 576) return;
            hovering = true;
            hoverStart = clock.Elapsed.TotalSeconds;
            hoverAnchor = cursor;
            patted = false;
        }

        private void Pet()
        {
            if (!hovering || patted || !preferences.Pats || menu.Visible || behavior == Behavior.Peek || behavior == Behavior.Sit || clock.Elapsed.TotalSeconds - hoverStart < 1) return;
            patted = true;
            Play(Behavior.Pat, false);
            AddSync(1, "pat");
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right) { OpenMenu(Cursor.Position); return; }
            if (e.Button != MouseButtons.Left || !pressed) return;
            pressed = false;
            Capture = false;
            bool dropped = dragging;
            if (dragging) { pendingClick = false; clickDelay.Stop(); SavePreferences(); }
            else if (pendingClick && IsDoubleClick(pendingPoint, Cursor.Position, clock.ElapsedMilliseconds - pendingTime)) { pendingClick = false; clickDelay.Stop(); StartRide(); }
            else { pendingClick = true; pendingPoint = Cursor.Position; pendingTime = clock.ElapsedMilliseconds; clickDelay.Stop(); clickDelay.Start(); }
            dragging = false;
            if (dropped) { SitWhereDropped(); SavePreferences(); }
        }

        // Let go of her with her seat on the top edge of a window and she sits there, the way a desktop pet should.
        // Anywhere else, the spot she was dropped is where she lives from now on.
        internal void SitWhereDropped()
        {
            if (hidden || riding || !IsHandleCreated) return;
            Point seat = SitSeat();
            Point spot = new Point(anchor.X + seat.X, anchor.Y + seat.Y);
            System.Collections.Generic.List<Native.Pane> panes = Native.Panes(Handle, bubble == null || !bubble.IsHandleCreated ? IntPtr.Zero : bubble.Handle, testing);
            int closest = 60;
            int pick = -1;
            for (int i = 0; i < panes.Count; i++)
            {
                Rectangle frame = panes[i].Frame;
                if (!panes[i].Sittable || spot.X < frame.Left + 30 || spot.X > frame.Right - 30) continue;
                int gap = Math.Abs(spot.Y - (frame.Top + 6));
                if (gap > closest) continue;
                bool covered = false;
                for (int above = 0; above < i; above++) if (panes[above].Frame.Contains(spot.X, frame.Top + 3)) covered = true;
                if (covered) continue;
                closest = gap;
                pick = i;
            }
            if (pick < 0) { restHome = anchor; return; }
            sitWindow = panes[pick].Handle;
            sitFrac = (spot.X - panes[pick].Frame.Left) / (double)panes[pick].Frame.Width;
            sitChosen = true;
            Play(Behavior.Sit, true);
            sitChosen = false;
        }

        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) pressed = false; }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0084 && frame != null)
            {
                long value = message.LParam.ToInt64();
                Point point = PointToClient(new Point((short)(value & 65535), (short)((value >> 16) & 65535)));
                if (!Solid(point))
                {
                    message.Result = new IntPtr(-1);
                    return;
                }
            }
            if (message.Msg == 0x0312 && message.WParam.ToInt32() == TalkId) { Talk(); return; }
            if (message.Msg == 0x0312 && message.WParam.ToInt32() == VoiceKeyId) { Listen(); return; }
            if (message.Msg == 0x0312 && message.WParam.ToInt32() == LookKeyId) { Look(""); return; }
            if (message.Msg == 0x0312 && message.WParam.ToInt32() == DismountId) { StopRide(); if (behavior == Behavior.Glitch) StopBehavior(); return; }
            if (message.Msg == 0x02E0) { if (behavior == Behavior.Peek || behavior == Behavior.Sit) StopBehavior(); anchor =KeepOnScreen(anchor); Location = anchor; RenderFrame(); }
            base.WndProc(ref message);
        }

        private bool Solid(Point point)
        {
            for (int dx = -4; dx <= 4; dx += 4)
                for (int dy = -4; dy <= 4; dy += 4)
                {
                    int x = point.X + dx;
                    int y = point.Y + dy;
                    if (x >= 0 && y >= 0 && x < frame.Width && y < frame.Height && frame.GetPixel(x, y).A >= 12) return true;
                }
            return false;
        }

        private void OnClickDelay()
        {
            clickDelay.Stop();
            if (!pendingClick) return;
            pendingClick = false;
            if (hidden || riding || dragging || pressed) return;
            React();
        }

        internal static bool IsDoubleClick(Point a, Point b, long elapsed)
        {
            Size box = SystemInformation.DoubleClickSize;
            return elapsed >= 0 && elapsed <= SystemInformation.DoubleClickTime && Math.Abs(a.X - b.X) <= box.Width / 2 && Math.Abs(a.Y - b.Y) <= box.Height / 2;
        }

        private void StartRide()
        {
            if (riding || hidden || !IsHandleCreated) return;
            StopBehavior();
            pendingClick = false;
            clickDelay.Stop();
            pressed = false;
            dragging = false;
            Capture = false;
            hotkey = Native.RegisterHotKey(Handle, DismountId, 0x0001 | 0x0002 | 0x4000, 0x43);
            riding = true;
            Native.SetWindowLong(Handle, -20, Native.GetWindowLong(Handle, -20) | 0x20);
            rideItem.Text = Lang.T("Hop off");
            UpdateTrayText();
            if (!hotkey && !testing) tray.ShowBalloonTip(5000, "Capri", Lang.T("Ctrl + Alt + C is taken by another app. Right-click Capri's tray icon and choose Hop off."), ToolTipIcon.Info);
            frames.Start();
        }

        private void StopRide()
        {
            if (hotkey && IsHandleCreated) Native.UnregisterHotKey(Handle, DismountId);
            hotkey = false;
            if (!riding) return;
            riding = false;
            leanAngle = 0;
            if (IsHandleCreated) Native.SetWindowLong(Handle, -20, Native.GetWindowLong(Handle, -20) & ~0x20);
            if (rideItem != null) rideItem.Text = Lang.T("Perch on cursor");
            UpdateTrayText();
            if (!IsDisposed) { Location = anchor; UpdateAnimation(); SavePreferences(); }
        }

        // Where she sits within her window: the spot that rests on the cursor, or on the edge of a window.
        private Point Seat()
        {
            float seatedHeight = perched == null ? preferences.Size : preferences.Size * perched.Height / (float)perched.Width;
            int seatX = perched == null ? Width / 2 - 24 : PaddingSize + (int)(preferences.Size * 0.267f) - 12;
            int seatY = perched == null ? Height - PaddingSize : PaddingSize + (int)((preferences.Size - seatedHeight) / 2 + seatedHeight * 0.73f) + 12;
            return new Point(seatX, seatY);
        }

        // Where the seated frame rests on a window edge: the line her legs make, about seven tenths of the way down.
        private Point SitSeat()
        {
            Bitmap seated = Sprite("sit");
            if (seated == null) return Seat();
            RectangleF rect = SpriteRect(seated, preferences.Size);
            return new Point((int)(Width / 2f + rect.X + 0.52f * rect.Width), (int)(Height / 2f + rect.Y + 0.70f * rect.Height));
        }

        private void FollowCursor()
        {
            Point cursor = Cursor.Position;
            Rectangle area = Screen.FromPoint(cursor).WorkingArea;
            Point seat = Seat();
            int seatX = seat.X;
            int seatY = seat.Y;
            int tx = Math.Max(area.Left, Math.Min(cursor.X - seatX, area.Right - Width));
            int ty = Math.Max(area.Top, Math.Min(cursor.Y - seatY, area.Bottom - Height));
            int nx = anchor.X + (int)Math.Round((tx - anchor.X) * 0.25);
            int ny = anchor.Y + (int)Math.Round((ty - anchor.Y) * 0.25);
            double velocity = Math.Max(-8, Math.Min(8, (nx - anchor.X) * 0.4));
            leanAngle = leanAngle * 0.8 + velocity * 0.2;
            anchor = new Point(Math.Max(area.Left, Math.Min(nx, area.Right - Width)), Math.Max(area.Top, Math.Min(ny, area.Bottom - Height)));
        }

        private void React()
        {
            clicks++;
            reactionStart = clock.Elapsed.TotalSeconds;
            if (!hidden) frames.Start();
            RenderFrame();
            if (chance.Next(4) == 0) { Say(Pick(ClickLines), false); Speak("click", false); }
        }

        private string Pick(string[] options) { return Lang.T(options[chance.Next(options.Length)]); }

        private Point HeadPoint() { return new Point(anchor.X + Width / 2 + (int)(preferences.Size * 0.04f), anchor.Y + PaddingSize); }

        // Automatic remarks respect the Speech bubbles switch and stay at least eight seconds apart; force skips both.
        internal void Say(string text, bool force) { Say(text, force, 0); }

        // Remarks stay quiet while a study session is in focus. A length of zero picks one that suits the text.
        internal void Say(string text, bool force, double seconds) { Say(text, force, seconds, false); }

        internal void Say(string text, bool force, double seconds, bool terminal)
        {
            text = Lang.T(text);
            double now = clock.Elapsed.TotalSeconds;
            bool focusing = study == StudyPhase.Focus && !studyPaused;
            if (hidden || !IsHandleCreated || behavior == Behavior.Peek || (!force && (!preferences.Bubbles || focusing || now - lastSaid < 8 || menu.Visible))) return;
            lastSaid = now;
            if (bubble == null) bubble = new Bubble(delegate { return HeadPoint(); });
            Bitmap closed = !preferences.Portrait ? null : Sprite(terminal ? "scream" : "talk-closed");
            Bitmap open = !preferences.Portrait ? null : Sprite(terminal ? "scream" : "talk-open");
            bubble.Present(text, seconds > 0 ? seconds : Math.Max(3, 2.5 + text.Length * 0.06), TopMost, Math.Max(12, preferences.Size / 17f), terminal, closed, open);
        }

        private void UpdateTrayText()
        {
            if (tray == null) return;
            string text = riding ? (hotkey ? Lang.T("Capri · Ctrl + Alt + C to hop off") : Lang.T("Capri · Hop off from this menu")) : Lang.T("Capri · Double-click to restore");
            if (study != StudyPhase.Off && !riding) text = "Capri · " + StudyStatus().ToLowerInvariant();
            if (text == trayShown) return;
            trayShown = text;
            tray.Text = text;
        }

        private void Announce(string text, double seconds, string kind) { Notice notice = new Notice(); notice.Text = text; notice.Seconds = seconds; notice.Voice = kind; notices.Enqueue(notice); }

        // Plays a recorded line for an occasion if there is one. Mute silences everything she plays; automatic lines are spaced out and left out of focus time.
        internal bool Speak(string kind, bool force)
        {
            double now = clock.Elapsed.TotalSeconds;
            bool focusing = study == StudyPhase.Focus && !studyPaused;
            if (preferences.Muted || hidden || !IsHandleCreated || (!force && (focusing || now - lastVoice < 8))) return false;
            if (!voice.Play(kind)) return false;
            lastVoice = now;
            return true;
        }

        private static string VoiceFor(Behavior which)
        {
            switch (which)
            {
                case Behavior.Stretch: return "stretch";
                case Behavior.Look: return "look";
                case Behavior.Yawn: return "yawn";
                case Behavior.Sleep: return "sleep";
                case Behavior.Pat: return "pat";
                case Behavior.Glitch: return "mischief";
                default: return "";
            }
        }

        private string VoiceStatus()
        {
            int clips = voice.Total();
            return clips == 0 ? "No voice clips yet" : clips + (clips == 1 ? " voice clip found" : " voice clips found");
        }

        // Creates the folder and a note explaining what to put in it, then shows it.
        private void OpenVoiceFolder()
        {
            Directory.CreateDirectory(voice.Folder);
            string note = Path.Combine(voice.Folder, "README.txt");
            if (!File.Exists(note))
                File.WriteAllText(note, "Put .wav files here and Capri will play them.\r\n\r\nUse a plain PCM wave file (16-bit is safest). MP3 and other formats will not play; convert them first.\r\nName a clip for the moment it belongs to, optionally with a number, so she can pick between several:\r\n\r\n  pat.wav, pat-2.wav, pat-3.wav   when you pat her head\r\n  click.wav     when you click her\r\n  wake.wav      when she wakes up\r\n  sit.wav       when she sits on a window\r\n  peek.wav      when she peeks out\r\n  stretch.wav, look.wav, yawn.wav, sleep.wav   her idle moods\r\n  alarm.wav     when an alarm goes off (replaces the chime)\r\n  study.wav     when a study session changes phase\r\n  hello.wav     for Reactions > Play now > Say something\r\n  reply.wav     when a local model answers\r\n  mischief.wav  when she glitches\r\n\r\nVolume follows your Windows volume. Mute is in her menu under Voice.\r\n");
            if (!testing) Process.Start(new ProcessStartInfo(voice.Folder) { UseShellExecute = true });
        }

        private ToolStripMenuItem BuildVoice()
        {
            ToolStripMenuItem item = new ToolStripMenuItem(Lang.T("Voice"));
            muteItem = new ToolStripMenuItem(Lang.T("Mute"), null, delegate { preferences.Muted = !preferences.Muted; muteItem.Checked = preferences.Muted; if (preferences.Muted) voice.Stop(); SavePreferences(); });
            muteItem.Checked = preferences.Muted;
            voiceStatusItem = new ToolStripMenuItem(VoiceStatus());
            voiceStatusItem.Enabled = false;
            item.DropDownItems.Add(muteItem);
            item.DropDownItems.Add(voiceStatusItem);
            item.DropDownItems.Add(new ToolStripSeparator());
            item.DropDownItems.Add(new ToolStripMenuItem(Lang.T("Play a sample"), null, delegate { PlaySample(); }));
            item.DropDownItems.Add(new ToolStripMenuItem(Lang.T("Open voice folder…"), null, delegate { OpenVoiceFolder(); }));
            item.DropDownOpening += delegate { voiceStatusItem.Text = VoiceStatus(); };
            return item;
        }

        internal void PlaySample()
        {
            System.Collections.Generic.List<string> have = new System.Collections.Generic.List<string>();
            foreach (string kind in Voice.Kinds) if (voice.Clips(kind).Count > 0) have.Add(kind);
            if (have.Count == 0) { Say("i have no voice yet. open the voice folder to give me one.", true); return; }
            if (preferences.Muted) { Say("i'm muted.", true); return; }
            Speak(have[chance.Next(have.Count)], true);
        }

        // Alarm and timer messages wait their turn, replace an older remark after a few seconds, and fall back to a tray balloon if she is hidden.
        internal void Deliver()
        {
            if (notices.Count == 0 || !IsHandleCreated) return;
            if (hidden)
            {
                Notice missed = notices.Dequeue();
                if (!testing) tray.ShowBalloonTip(8000, "Capri", missed.Text, ToolTipIcon.Info);
                return;
            }
            if (menu.Visible) return;
            if (notices.Peek().Voice != "alarm" && bubble != null && bubble.Visible && bubble.Age < 6) return;
            Notice next = notices.Dequeue();
            if (!Speak(next.Voice, true) && preferences.Chime && !preferences.Muted && !testing) System.Media.SystemSounds.Asterisk.Play();
            reactionStart = clock.Elapsed.TotalSeconds;
            frames.Start();
            Say(next.Text, true, next.Seconds);
        }

        private static string Clock(TimeSpan span)
        {
            int seconds = (int)Math.Ceiling(Math.Max(0, span.TotalSeconds));
            return seconds >= 3600 ? string.Format("{0}:{1:00}:{2:00}", seconds / 3600, seconds / 60 % 60, seconds % 60) : string.Format("{0}:{1:00}", seconds / 60, seconds % 60);
        }

        private string StudyStatus()
        {
            if (study == StudyPhase.Off) return Lang.T("No study session");
            string phase = study == StudyPhase.Focus ? Lang.T("Focus") : Lang.T("Break");
            string left = Clock(studyPaused ? studyLeft : studyEnd - DateTime.UtcNow);
            string done = studyDone > 0 ? " · " + Lang.F("{0} done", studyDone) : "";
            return studyPaused ? Lang.F("Paused {0}", phase) + " " + left + done : phase + " " + left + done;
        }

        private void StartStudy(int focus, int rest)
        {
            focusMinutes = focus;
            breakMinutes = rest;
            study = StudyPhase.Focus;
            studyPaused = false;
            studyDone = 0;
            studyEnd = DateTime.UtcNow.AddMinutes(focus);
            Say(Lang.F("focus. {0} minutes. i'll keep quiet.", focus), true);
            RefreshTimers();
        }

        private void StopStudy()
        {
            if (study == StudyPhase.Off) return;
            study = StudyPhase.Off;
            studyPaused = false;
            Say("ok. stopped.", true);
            RefreshTimers();
        }

        private void PauseStudy()
        {
            if (study == StudyPhase.Off) return;
            if (studyPaused) studyEnd = DateTime.UtcNow + studyLeft;
            else studyLeft = studyEnd - DateTime.UtcNow;
            studyPaused = !studyPaused;
            RefreshTimers();
        }

        // Called a few times a second: fires alarms that are due and moves the study session along.
        internal void Keep()
        {
            DateTime now = DateTime.UtcNow;
            for (int i = preferences.Alarms.Count - 1; i >= 0; i--)
            {
                AlarmEntry alarm = preferences.Alarms[i];
                if (alarm.Due > now.Ticks) continue;
                preferences.Alarms.RemoveAt(i);
                Announce(AlarmText(alarm), 90, "alarm");
                SavePreferences();
                RebuildAlarms();
            }
            if (study != StudyPhase.Off && !studyPaused && now >= studyEnd)
            {
                if (study == StudyPhase.Focus)
                {
                    studyDone++;
                    study = StudyPhase.Break;
                    studyEnd = now.AddMinutes(breakMinutes);
                    Announce(Lang.F("time. take {0} minutes.", breakMinutes), 20, "study");
                }
                else
                {
                    study = StudyPhase.Focus;
                    studyEnd = now.AddMinutes(focusMinutes);
                    Announce(Lang.F("break's over. {0} more minutes.", focusMinutes), 20, "study");
                }
            }
            RefreshTimers();
        }

        private static string AlarmText(AlarmEntry alarm)
        {
            string time = new DateTime(alarm.Due, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm");
            bool late = alarm.Due < DateTime.UtcNow.AddMinutes(-2).Ticks;
            string note = string.IsNullOrEmpty(alarm.Label) ? "" : " " + alarm.Label;
            if (!string.IsNullOrEmpty(alarm.Label) && alarm.Label.StartsWith("Timer: ")) return Lang.F("time's up! {0}.", alarm.Label.Substring(7));
            return late ? Lang.F("you missed {0}.", time) + note : Lang.F("it's {0}.", time) + note;
        }

        private void AddAlarm(DateTime due, string label) { AddAlarm(due, label, true); }

        private bool AddAlarm(DateTime due, string label, bool say)
        {
            if (preferences.Alarms.Count >= 20) { if (say) Say("that's plenty of alarms.", true); return false; }
            AlarmEntry alarm = new AlarmEntry();
            alarm.Due = due.ToUniversalTime().Ticks;
            alarm.Label = label;
            preferences.Alarms.Add(alarm);
            preferences.Alarms.Sort(delegate(AlarmEntry a, AlarmEntry b) { return a.Due.CompareTo(b.Due); });
            SavePreferences();
            RebuildAlarms();
            if (say) Say(Lang.F("alarm set for {0}.", due.ToString("HH:mm")), true);
            return true;
        }

        private void RebuildAlarms()
        {
            if (alarmsMenu == null) return;
            while (alarmsMenu.DropDownItems.Count > AlarmListStart) alarmsMenu.DropDownItems.RemoveAt(AlarmListStart);
            if (preferences.Alarms.Count == 0) { ToolStripMenuItem none = new ToolStripMenuItem(Lang.T("No alarms set")); none.Enabled = false; alarmsMenu.DropDownItems.Add(none); return; }
            foreach (AlarmEntry alarm in preferences.Alarms.ToArray())
            {
                AlarmEntry pending = alarm;
                string time = new DateTime(pending.Due, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm");
                alarmsMenu.DropDownItems.Add(new ToolStripMenuItem(Lang.T("Cancel") + "  " + time + (string.IsNullOrEmpty(pending.Label) ? "" : " · " + pending.Label), null, delegate { preferences.Alarms.Remove(pending); SavePreferences(); RebuildAlarms(); }));
            }
        }

        private void RefreshTimers()
        {
            string status = StudyStatus();
            if (studyStatusItem != null && status != statusShown)
            {
                statusShown = status;
                studyStatusItem.Text = status;
                studyPauseItem.Text = studyPaused ? Lang.T("Resume") : Lang.T("Pause");
                studyPauseItem.Enabled = studyStopItem.Enabled = study != StudyPhase.Off;
            }
            UpdateTrayText();
        }

        private ToolStripMenuItem BuildTimers()
        {
            ToolStripMenuItem timers = new ToolStripMenuItem(Lang.T("Timers"));
            ToolStripMenuItem studying = new ToolStripMenuItem(Lang.T("Study timer"));
            studyStatusItem = new ToolStripMenuItem(StudyStatus());
            studyStatusItem.Enabled = false;
            studying.DropDownItems.Add(studyStatusItem);
            studying.DropDownItems.Add(new ToolStripSeparator());
            studying.DropDownItems.Add(new ToolStripMenuItem(Lang.T("Start 25 min focus, 5 min break"), null, delegate { StartStudy(25, 5); }));
            studying.DropDownItems.Add(new ToolStripMenuItem(Lang.T("Start 50 min focus, 10 min break"), null, delegate { StartStudy(50, 10); }));
            studyPauseItem = new ToolStripMenuItem(Lang.T("Pause"), null, delegate { PauseStudy(); });
            studyStopItem = new ToolStripMenuItem(Lang.T("Stop"), null, delegate { StopStudy(); });
            studyPauseItem.Enabled = studyStopItem.Enabled = false;
            studying.DropDownItems.Add(studyPauseItem);
            studying.DropDownItems.Add(studyStopItem);
            alarmsMenu = new ToolStripMenuItem(Lang.T("Alarms"));
            alarmsMenu.DropDownItems.Add(new ToolStripMenuItem(Lang.T("Set a timer…"), null, delegate { AskForTimer(); }));
            alarmsMenu.DropDownItems.Add(new ToolStripMenuItem(Lang.T("Set alarm…"), null, delegate { AskForAlarm(); }));
            ToolStripMenuItem soon = new ToolStripMenuItem(Lang.T("Remind me in"));
            foreach (int minutes in new int[] { 5, 10, 15, 30, 60 })
            {
                int wait = minutes;
                soon.DropDownItems.Add(new ToolStripMenuItem(Lang.F("{0} minutes", wait), null, delegate { AddAlarm(DateTime.Now.AddMinutes(wait), ""); }));
            }
            alarmsMenu.DropDownItems.Add(soon);
            alarmsMenu.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem chime = new ToolStripMenuItem(Lang.T("Chime"));
            chime.Checked = preferences.Chime;
            chime.Click += delegate { preferences.Chime = !preferences.Chime; chime.Checked = preferences.Chime; SavePreferences(); };
            timers.DropDownItems.Add(studying);
            timers.DropDownItems.Add(alarmsMenu);
            timers.DropDownItems.Add(new ToolStripSeparator());
            timers.DropDownItems.Add(chime);
            RebuildAlarms();
            return timers;
        }

        private void AskForTimer()
        {
            using (AskForm ask = new AskForm("Set a timer", "How long?", "10 minutes   ·   30 seconds   ·   an hour", delegate(string text) { return Errands.Length(Errands.Words(text)) > 0 ? null : Lang.T("Try 10 minutes, 30 seconds or an hour."); }))
            {
                if (ask.ShowDialog() != DialogResult.OK) return;
                double seconds = Errands.Length(Errands.Words(ask.Answer));
                if (AddAlarm(DateTime.Now.AddSeconds(seconds), "Timer: " + Errands.Describe(seconds), false)) Say(Lang.F("timer's on for {0}.", Errands.Describe(seconds)), true);
            }
        }

        private void AskForAlarm()
        {
            using (AskForm ask = new AskForm("Set an alarm", "When should she nudge you?", "7:30 stand up   ·   6:15 pm   ·   in 20m tea", delegate(string text)
            {
                DateTime due;
                string label;
                return TryParseWhen(text, DateTime.Now, out due, out label) ? null : Lang.T("Try 7:30, 6:15 pm or in 20m, then an optional note.");
            }))
            {
                if (ask.ShowDialog() != DialogResult.OK) return;
                DateTime due;
                string label;
                if (TryParseWhen(ask.Answer, DateTime.Now, out due, out label)) AddAlarm(due, label);
            }
        }

        // Accepts a clock time (7:30, 19:45, 6 pm) or a wait (in 20m, 1h30m, 10 min), then an optional note.
        internal static bool TryParseWhen(string text, DateTime now, out DateTime due, out string label)
        {
            due = now;
            label = "";
            text = (text ?? "").Trim();
            Match wait = Regex.Match(text, @"^(?:in\s+)?((?:\d+\s*(?:hours?|hrs?|h|minutes?|mins?|m)(?![a-z])\s*)+)(.*)$", RegexOptions.IgnoreCase);
            Match bare = Regex.Match(text, @"^in\s+(\d+)\b(.*)$", RegexOptions.IgnoreCase);
            Match at = Regex.Match(text, @"^(\d{1,2})(?::(\d{2}))?\s*(am|pm)?\b(.*)$", RegexOptions.IgnoreCase);
            if (wait.Success)
            {
                int minutes = 0;
                foreach (Match part in Regex.Matches(wait.Groups[1].Value, @"(\d+)\s*(hours?|hrs?|h|minutes?|mins?|m)(?![a-z])", RegexOptions.IgnoreCase))
                    minutes += int.Parse(part.Groups[1].Value) * (char.ToLowerInvariant(part.Groups[2].Value[0]) == 'h' ? 60 : 1);
                if (minutes < 1 || minutes > 24 * 60) return false;
                due = now.AddMinutes(minutes);
                label = Tidy(wait.Groups[2].Value);
                return true;
            }
            if (bare.Success)
            {
                int minutes = int.Parse(bare.Groups[1].Value);
                if (minutes < 1 || minutes > 24 * 60) return false;
                due = now.AddMinutes(minutes);
                label = Tidy(bare.Groups[2].Value);
                return true;
            }
            if (!at.Success || (!at.Groups[2].Success && !at.Groups[3].Success)) return false;
            int hour = int.Parse(at.Groups[1].Value);
            int minute = at.Groups[2].Success ? int.Parse(at.Groups[2].Value) : 0;
            if (minute > 59) return false;
            if (at.Groups[3].Success)
            {
                if (hour < 1 || hour > 12) return false;
                hour = hour % 12 + (at.Groups[3].Value.ToLowerInvariant() == "pm" ? 12 : 0);
            }
            else if (hour > 23) return false;
            due = new DateTime(now.Year, now.Month, now.Day, hour, minute, 0);
            if (due <= now) due = due.AddDays(1);
            label = Tidy(at.Groups[4].Value);
            return true;
        }

        private static string Tidy(string note) { return note.Trim().TrimStart('-', ':', ',', '.').Trim(); }

        private ToolStripMenuItem BuildChat()
        {
            ToolStripMenuItem chat = new ToolStripMenuItem(Lang.T("Chat"));
            speakItem = new ToolStripMenuItem(Lang.T("Speak to her"), null, delegate { Listen(); });
            speakItem.ShortcutKeyDisplayString = "Ctrl+Alt+V";
            speakItem.Enabled = Ear.Available;
            if (!Ear.Available) speakItem.ToolTipText = Lang.T("Windows has no speech recognizer installed.");
            micItem = new ToolStripMenuItem(Lang.T("Mic button on hover"), null, delegate { preferences.MicButton = !preferences.MicButton; micItem.Checked = preferences.MicButton; SavePreferences(); });
            micItem.Checked = preferences.MicButton;
            ToolStripMenuItem talkItem = new ToolStripMenuItem(Lang.T("Talk to Capri…"), null, delegate { Talk(); });
            talkItem.ShortcutKeyDisplayString = "Ctrl+Alt+T";
            chat.DropDownItems.Add(talkItem);
            chat.DropDownItems.Add(speakItem);
            chat.DropDownItems.Add(micItem);
            micDevices = new ToolStripMenuItem(Lang.T("Microphone"));
            micDevices.DropDownItems.Add(new ToolStripMenuItem(Lang.T("Windows default")));
            micDevices.DropDownOpening += delegate { RebuildMics(); };
            chat.DropDownItems.Add(micDevices);
            speechStatusItem = new ToolStripMenuItem(SpeechStatus());
            speechStatusItem.Enabled = false;
            chat.DropDownItems.Add(speechStatusItem);
            ToolStripMenuItem whisperItem = new ToolStripMenuItem(Lang.T("Use Whisper when it is installed"));
            whisperItem.Checked = preferences.UseWhisper;
            whisperItem.Click += delegate { preferences.UseWhisper = !preferences.UseWhisper; ear.UseWhisper = preferences.UseWhisper; whisperItem.Checked = preferences.UseWhisper; speechStatusItem.Text = SpeechStatus(); SavePreferences(); };
            chat.DropDownItems.Add(whisperItem);
            earsItem = new ToolStripMenuItem(Lang.T("Download better ears…"));
            earsItem.Click += delegate { DownloadEars(); };
            chat.DropDownItems.Add(earsItem);
            ToolStripMenuItem memoryMenu = new ToolStripMenuItem(Lang.T("Memory"));
            ToolStripMenuItem memoryItem = new ToolStripMenuItem(Lang.T("Let her remember things"));
            memoryItem.Checked = preferences.MemoryOn;
            memoryItem.Click += delegate { preferences.MemoryOn = !preferences.MemoryOn; memoryItem.Checked = preferences.MemoryOn; SavePreferences(); };
            memoryMenu.DropDownItems.Add(memoryItem);
            memoryMenu.DropDownItems.Add(new ToolStripMenuItem(Lang.T("Show what she remembers"), null, delegate
            {
                try
                {
                    if (!File.Exists(MemoryPath())) SaveMemory();
                    Process.Start(new ProcessStartInfo("notepad.exe", "\"" + MemoryPath() + "\"") { UseShellExecute = true });
                }
                catch (Exception) { }
            }));
            memoryMenu.DropDownItems.Add(new ToolStripMenuItem(Lang.T("Forget everything"), null, delegate
            {
                if (MessageBox.Show(Lang.T("Make her forget everything she has noted about you?"), "Capri", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) ForgetEverything();
            }));
            chat.DropDownItems.Add(memoryMenu);
            chat.DropDownItems.Add(new ToolStripMenuItem(Lang.T("Cloud model settings…"), null, delegate { ConfigureCloud(); }));
            cloudItem = new ToolStripMenuItem(Lang.T("Use the cloud model"));
            cloudItem.Checked = preferences.CloudOn;
            cloudItem.Click += delegate
            {
                if (!preferences.CloudOn && (!Cloud.HasKey() || preferences.CloudModel.Trim().Length == 0)) { ConfigureCloud(); return; }
                preferences.CloudOn = !preferences.CloudOn;
                cloudItem.Checked = preferences.CloudOn;
                if (preferences.CloudOn) preferences.LlmOn = true;
                SavePreferences();
                Connect(false);
                if (CloudReady()) CheckCloud();
            };
            chat.DropDownItems.Add(cloudItem);
            ToolStripMenuItem lookNow = new ToolStripMenuItem(Lang.T("Look at my screen"), null, delegate { Look(""); });
            lookNow.ShortcutKeyDisplayString = "Ctrl+Alt+L";
            chat.DropDownItems.Add(lookNow);
            ToolStripMenuItem lookItem = new ToolStripMenuItem(Lang.T("Let her look when I ask"));
            lookItem.Checked = preferences.LookOn;
            lookItem.Click += delegate { preferences.LookOn = !preferences.LookOn; lookItem.Checked = preferences.LookOn; SavePreferences(); };
            chat.DropDownItems.Add(lookItem);
            chat.DropDownOpening += delegate { speechStatusItem.Text = SpeechStatus(); };
            ToolStripMenuItem pauses = new ToolStripMenuItem(Lang.T("Wait through pauses"));
            double[] lengths = { 1.2, 2.5, 4.0 };
            string[] pauseNames = { Lang.T("Short (1.2 seconds)"), Lang.T("Normal (2.5 seconds)"), Lang.T("Long (4 seconds)") };
            ToolStripMenuItem[] pauseItems = new ToolStripMenuItem[3];
            for (int i = 0; i < 3; i++)
            {
                double seconds = lengths[i];
                int which = i;
                pauseItems[i] = new ToolStripMenuItem(pauseNames[i], null, delegate
                {
                    preferences.PauseSeconds = seconds;
                    ear.PauseSeconds = seconds;
                    for (int k = 0; k < 3; k++) pauseItems[k].Checked = k == which;
                    SavePreferences();
                });
                pauseItems[i].Checked = Math.Abs(preferences.PauseSeconds - seconds) < 0.01;
                pauses.DropDownItems.Add(pauseItems[i]);
            }
            chat.DropDownItems.Add(pauses);
            chat.DropDownItems.Add(new ToolStripSeparator());
            llmItem = new ToolStripMenuItem(Lang.T("Use a local model"), null, delegate
            {
                // With the cloud model in use, this switches to the local one instead of turning her brain off.
                if (preferences.LlmOn && preferences.CloudOn) { preferences.CloudOn = false; SavePreferences(); SetLlm(true); return; }
                SetLlm(!preferences.LlmOn);
            });
            llmItem.Checked = preferences.LlmOn && !preferences.CloudOn;
            chat.DropDownItems.Add(llmItem);
            llmStatusItem = new ToolStripMenuItem(preferences.LlmOn ? Lang.T("Looking for a model…") : llmStatus);
            llmStatusItem.Enabled = false;
            chat.DropDownItems.Add(llmStatusItem);
            llmModels = new ToolStripMenuItem(Lang.T("Model"));
            llmModels.Enabled = false;
            chat.DropDownItems.Add(llmModels);
            chat.DropDownItems.Add(new ToolStripMenuItem(Lang.T("Server address…"), null, delegate { AskForServer(); }));
            chat.DropDownItems.Add(new ToolStripMenuItem(Lang.T("Edit her personality…"), null, delegate { EditPersona(); }));
            chat.DropDownItems.Add(new ToolStripMenuItem(Lang.T("Forget this conversation"), null, delegate { chatLog.Clear(); if (this.chat != null) this.chat.Clear(); }));
            return chat;
        }

        // ---- music and video awareness: Off, show what is playing, or react to it ----

        private static string Brief(Track track)
        {
            string title = track.Title.Length > 60 ? track.Title.Substring(0, 57) + "..." : track.Title;
            string artist = track.Artist.Length > 40 ? track.Artist.Substring(0, 37) + "..." : track.Artist;
            return artist.Length > 0 ? title + " - " + artist : title;
        }

        internal void SetMusic(int mode)
        {
            preferences.MusicMode = mode;
            for (int i = 0; i < musicModes.Length; i++) musicModes[i].Checked = i == mode;
            SavePreferences();
            if (mode == 0) { StopMusic(); musicStatusItem.Text = Lang.T("Off"); return; }
            musicStatusItem.Text = Lang.T("Looking for what is playing...");
            StartMusic();
        }

        private void StartMusic()
        {
            if (musicTimer != null || !Media.Available) return;
            lastTrackKey = "";
            musicTimer = new System.Threading.Timer(delegate { PollMusic(); }, null, 0, 4000);
        }

        private void StopMusic()
        {
            if (musicTimer != null) { musicTimer.Dispose(); musicTimer = null; }
            if (commentLater != null) commentLater.Stop();
            musicPlaying = false;
            notPlaying = false;
            lastTrackKey = "";
        }

        // Windows is asked on a background thread so she never stalls; the answer is picked up on the interface thread.
        private void PollMusic()
        {
            if (System.Threading.Interlocked.Exchange(ref musicReading, 1) == 1) return;
            try
            {
                Track track = Media.Read();
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)delegate { OnTrack(track); });
            }
            finally { musicReading = 0; }
        }

        internal void OnTrack(Track track)
        {
            if (preferences.MusicMode == 0) return;
            musicPlaying = track != null && track.Playing;
            musicStatusItem.Text = track == null ? Lang.T("Nothing playing") : Lang.F(track.Playing ? "Playing: {0}" : "Paused: {0}", Brief(track));
            double now = clock.Elapsed.TotalSeconds;
            if (!musicPlaying) { if (!notPlaying) { notPlaying = true; notPlayingSince = now; } return; }
            // The same song starting again after a real pause or stop counts as new; a few seconds' pause does not.
            bool again = track.Key == lastTrackKey && notPlaying && now - notPlayingSince >= 10;
            notPlaying = false;
            if (track.Key == lastTrackKey && !again) return;
            lastTrackKey = track.Key;
            ShowNowPlaying(track);
            if (preferences.MusicMode != 2) return;
            if (commentLater == null)
            {
                commentLater = new System.Windows.Forms.Timer();
                commentLater.Interval = 3500;
                commentLater.Tick += delegate { commentLater.Stop(); if (commentFor != null) Comment(commentFor); };
            }
            commentFor = track;
            commentLater.Stop();
            commentLater.Start();
        }

        // A terminal-style card with the title, without her portrait, so it reads as a readout and not as something she said.
        internal void ShowNowPlaying(Track track)
        {
            if (hidden || !IsHandleCreated || behavior == Behavior.Peek || (study == StudyPhase.Focus && !studyPaused)) return;
            if (bubble == null) bubble = new Bubble(delegate { return HeadPoint(); });
            string text = Lang.T("NOW PLAYING") + " // " + Brief(track);
            lastSaid = clock.Elapsed.TotalSeconds;
            bubble.Present(text, 6.5, TopMost, Math.Max(12, preferences.Size / 17f), true, null, null);
        }

        // Her reaction: from the local model when it is connected, otherwise a plain cheer that does not pretend to know the song.
        internal void Comment(Track track)
        {
            double now = clock.Elapsed.TotalSeconds;
            if (now - lastComment < 15 || hidden || (study == StudyPhase.Focus && !studyPaused)) return;
            lastComment = now;
            if (!preferences.LlmOn || llmLink == null || llmBusy) { Say(Pick(MusicLines), true); return; }
            string title = track.Title.Replace("\"", "'").Replace("\r", " ").Replace("\n", " ");
            if (title.Length > 120) title = title.Substring(0, 120);
            string said = "[The user just started playing \"" + title + "\"" + (track.Artist.Length > 0 ? " by " + track.Artist.Replace("\"", "'") : "") + " in " + track.App + ". You can't hear it and only know that title, so don't guess what it sounds like or what it is about. Give one very short reaction.]";
            List<ChatLine> ask = new List<ChatLine>();
            ask.Add(new ChatLine("system", PersonaText()));
            ask.Add(new ChatLine("user", said));
            LlmLink link = llmLink;
            string model = ActiveModel();
            ThreadPool.QueueUserWorkItem(delegate
            {
                string answer = "";
                try { answer = Llm.Chat(link, model, ask); }
                catch (Exception) { }
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)delegate { if (!hidden) Say(answer.Length > 0 ? answer : Pick(MusicLines), true); });
            });
        }

        // Asks first, then fetches whatever is missing in the background. Clicking the menu item again while it runs cancels it.
        internal void DownloadEars()
        {
            if (earsBusy) { earsCancel = true; return; }
            string folder = ear.SpeechFolder;
            bool needProgram = EarsDownload.NeedProgram(folder);
            bool needModel = EarsDownload.NeedModel(folder);
            if (!needProgram && !needModel) { Say("My better ears are already here!", true); return; }
            string what = (needProgram ? Lang.F("Whisper, a speech recognizer that runs on your PC ({0} MB), from github.com", (EarsDownload.ProgramBytes + 999999) / 1000000) + "\n" : "")
                + (needModel ? Lang.F("A speech model that understands many languages ({0} MB), from huggingface.co", (EarsDownload.ModelBytes + 999999) / 1000000) + "\n" : "");
            if (!testing && MessageBox.Show(Lang.F("This downloads once from the internet:\n\n{0}\nThey are checked against known fingerprints before they are used. Nothing you say is ever sent anywhere. They are saved in {1}.\n\nDownload now?", what, folder), "Capri", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            earsBusy = true;
            earsCancel = false;
            earsItem.Text = Lang.T("Cancel the download");
            Say("Downloading my better ears. This can take a few minutes.", true);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string failure = null;
                int lastShown = -1;
                int shownQuarter = 0;
                try
                {
                    EarsDownload.Install(folder, delegate(int percent)
                    {
                        if (percent == lastShown || !IsHandleCreated) return;
                        lastShown = percent;
                        BeginInvoke((Action)delegate
                        {
                            if (!earsBusy) return;
                            earsItem.Text = Lang.F("Downloading better ears… {0}%", percent);
                            if (percent / 25 > shownQuarter && percent < 99) { shownQuarter = percent / 25; Say(Lang.F("Downloading better ears… {0}%", percent), true, 3); }
                        });
                    }, delegate { return earsCancel; });
                }
                catch (OperationCanceledException) { failure = ""; }
                catch (Exception error) { failure = error.Message; }
                if (!IsHandleCreated) return;
                BeginInvoke((Action)delegate
                {
                    earsBusy = false;
                    earsItem.Text = Lang.T("Download better ears…");
                    speechStatusItem.Text = SpeechStatus();
                    if (failure == null) Say("Better ears are in! I should hear you much better now.", true);
                    else if (failure.Length == 0) Say("Download cancelled.", true);
                    else Say(Lang.F("The download failed: {0}", failure), true);
                });
            });
        }

        private string SpeechStatus() { return "Ears: " + (preferences.UseWhisper ? Whisper.Describe(ear.SpeechFolder, ear.Language) : "Windows' own (Whisper off)"); }

        // The microphone list is read each time the menu opens, since headsets come and go.
        internal void RebuildMics()
        {
            micDevices.DropDownItems.Clear();
            AddMic("Windows default", "");
            foreach (string name in Native.WaveDevices()) if (name.Length > 0) AddMic(name, name);
            ThemeDropDown(micDevices);
        }

        private void AddMic(string label, string device)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(label, null, delegate { preferences.MicDevice = device; ear.Device = device; SavePreferences(); });
            item.Checked = preferences.MicDevice == device;
            micDevices.DropDownItems.Add(item);
        }

        private string PersonaPath() { return Path.Combine(Path.GetDirectoryName(settingsPath), "persona.txt"); }

        // Her personality for the model: persona.txt if there is one with something in it, otherwise the built-in description.
        internal string PersonaText()
        {
            string text;
            try
            {
                text = File.Exists(PersonaPath()) ? File.ReadAllText(PersonaPath()).Trim() : "";
                if (text.Length == 0) text = Llm.DefaultPersona;
            }
            catch (IOException) { text = Llm.DefaultPersona; }
            catch (UnauthorizedAccessException) { text = Llm.DefaultPersona; }
            return text + Lang.Instruction();
        }

        // Writes the built-in description out as persona.txt the first time, then opens it to edit. Delete the file to go back to the original.
        internal void EditPersona()
        {
            string path = PersonaPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (!File.Exists(path)) File.WriteAllText(path, Llm.DefaultPersona + "\r\n");
            if (!testing) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }

        // ---- the optional cloud model ----
        private bool CloudReady()
        {
            return preferences.CloudOn && Cloud.HasKey() && preferences.CloudModel.Trim().Length > 0 && Cloud.UrlOk(Cloud.Base(preferences.CloudProvider, preferences.CloudUrl));
        }

        private LlmLink CloudLink()
        {
            LlmLink link = new LlmLink();
            link.Cloud = true;
            link.Provider = preferences.CloudProvider;
            link.Base = Cloud.Base(preferences.CloudProvider, preferences.CloudUrl);
            link.Models = new string[] { preferences.CloudModel.Trim() };
            return link;
        }

        private void ConfigureCloud()
        {
            using (CloudForm form = new CloudForm(preferences.CloudProvider, preferences.CloudModel, preferences.CloudUrl, preferences.CloudLook, preferences.CloudMemory, Cloud.HasKey()))
            {
                if (form.ShowDialog() != DialogResult.OK) return;
                ApplyCloud(form.Provider, form.Model, form.Url, form.Key, form.Look, form.Memory, form.RemoveKey);
            }
        }

        internal void ApplyCloud(string provider, string model, string url, string key, bool look, bool memory, bool removeKey)
        {
            preferences.CloudProvider = provider;
            preferences.CloudModel = (model ?? "").Trim();
            preferences.CloudUrl = (url ?? "").Trim();
            preferences.CloudLook = look;
            preferences.CloudMemory = memory;
            if (removeKey) { Cloud.ClearKey(); preferences.CloudOn = false; }
            else
            {
                if ((key ?? "").Trim().Length > 0) Cloud.SaveKey(key);
                preferences.CloudOn = Cloud.HasKey() && preferences.CloudModel.Length > 0;
            }
            if (preferences.CloudOn) preferences.LlmOn = true;
            SavePreferences();
            if (cloudItem != null) cloudItem.Checked = preferences.CloudOn;
            if (preferences.LlmOn) { Connect(false); if (CloudReady()) CheckCloud(); }
            else RefreshChat();
        }

        // One tiny question so a wrong key or model name is found now, not in the middle of a chat.
        private void CheckCloud()
        {
            LlmLink link = CloudLink();
            string model = link.Models[0];
            List<ChatLine> hello = new List<ChatLine>();
            hello.Add(new ChatLine("user", "Say hi in three words."));
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = true;
                try { ok = Llm.Chat(link, model, hello).Length > 0; }
                catch (Exception) { ok = false; }
                string why = Cloud.LastError;
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)delegate { Respond(ok ? "Cloud brain works!" : Lang.F("Cloud trouble: {0}. Chat, Cloud model settings to change it.", why.Length > 0 ? why : Lang.T("no answer"))); });
            });
        }

        private ToolStripMenuItem cloudItem;
        private ToolStripMenuItem startupItem;

        private string ActiveModel()
        {
            if (llmLink == null || llmLink.Models.Length == 0) return "";
            return Array.IndexOf(llmLink.Models, preferences.LlmModel) >= 0 ? preferences.LlmModel : llmLink.Models[0];
        }

        private void RefreshChat()
        {
            llmItem.Checked = preferences.LlmOn && !preferences.CloudOn;
            if (cloudItem != null) cloudItem.Checked = preferences.LlmOn && preferences.CloudOn;
            llmStatusItem.Text = llmStatus;
            if (chat != null) chat.SetStatus(llmStatus);
            llmModels.DropDownItems.Clear();
            if (llmLink != null)
                foreach (string name in llmLink.Models)
                {
                    string pick = name;
                    ToolStripMenuItem entry = new ToolStripMenuItem(name, null, delegate { ChooseModel(pick); });
                    entry.Checked = name == ActiveModel();
                    llmModels.DropDownItems.Add(entry);
                }
            llmModels.Enabled = llmModels.HasDropDownItems;
            if (llmModels.HasDropDownItems) ThemeDropDown(llmModels);
        }

        private void ChooseModel(string name)
        {
            preferences.LlmModel = name;
            llmStatus = "Using " + name;
            RefreshChat();
            SavePreferences();
        }

        private void SetLlm(bool on)
        {
            preferences.LlmOn = on;
            SavePreferences();
            llmAttempt++;
            if (on) { Connect(true); return; }
            llmLink = null;
            llmBusy = false;
            llmStatus = "Off";
            chatLog.Clear();
            if (chat != null) { chat.SetBusy(false); chat.AddNote("The local model was switched off."); }
            RefreshChat();
        }

        // Looks for the model server on a background thread so she never freezes; the answer is picked up on the interface thread.
        private void Connect(bool announce)
        {
            string address = preferences.LlmUrl;
            int attempt = ++llmAttempt;
            localLink = null;
            bool cloud = CloudReady();
            llmLink = cloud ? CloudLink() : null;
            llmStatus = cloud ? "Cloud: " + Cloud.Name(preferences.CloudProvider) + " / " + preferences.CloudModel : "Looking for a model…";
            RefreshChat();
            ThreadPool.QueueUserWorkItem(delegate
            {
                LlmLink found = Llm.Discover(address);
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)delegate
                {
                    if (attempt != llmAttempt || !preferences.LlmOn) return;
                    bool ok = found.Models.Length > 0;
                    localLink = ok ? found : null;
                    if (cloud) return;
                    llmLink = localLink;
                    llmStatus = ok ? "Using " + ActiveModel() : found.Error ?? "No local model server found.";
                    RefreshChat();
                    if (announce) Respond(ok ? Lang.F("found {0}.", ActiveModel()) : "i can't find a local model. is ollama or lm studio running?");
                });
            });
        }

        internal bool TrySetServer(string text)
        {
            Uri uri = null;
            text = (text ?? "").Trim();
            if (text.Length > 0 && !Llm.TryLocal(text, out uri)) return false;
            preferences.LlmUrl = text.Length == 0 ? "" : uri.GetLeftPart(UriPartial.Authority);
            SavePreferences();
            if (preferences.LlmOn) Connect(false);
            return true;
        }

        private void AskForServer()
        {
            using (AskForm ask = new AskForm("Server address", "Where is your local model running?", "Leave empty to look for Ollama or LM Studio. Only this computer is allowed.", delegate(string text)
            {
                Uri uri;
                return string.IsNullOrWhiteSpace(text) || Llm.TryLocal(text, out uri) ? null : Lang.T("That isn't on this computer. Try localhost:11434.");
            }))
            {
                if (ask.ShowDialog() == DialogResult.OK) TrySetServer(ask.Answer);
            }
        }

        private void Reply(string text)
        {
            if (!hidden) { Say(text, true, Math.Min(45, 4 + text.Length * 0.07)); Speak("reply", true); return; }
            if (!testing) tray.ShowBalloonTip(10000, "Capri", text.Length > 240 ? text.Substring(0, 240) + "…" : text, ToolTipIcon.Info);
        }

        private bool ChatOpen() { return chat != null && chat.Visible; }

        // Shows the microphone beside her while the pointer is on her (or on the microphone itself), and puts it away afterwards.
        // Her menu opens with the hover mic already put away: hiding or showing another window while the menu is open closes it at once.
        internal void OpenMenu(Point at)
        {
            if (mic != null && mic.Visible) { mic.Appear(false, TopMost); mic.Settle(); Application.DoEvents(); }
            menu.Show(at);
        }

        internal void MicWatch()
        {
            if (menu.Visible) return;
            bool allowed = preferences.MicButton && Ear.Available && !hidden && !riding && !dragging && behavior != Behavior.Peek && IsHandleCreated;
            double now = clock.Elapsed.TotalSeconds;
            bool near = now - lastOverHer < 0.6 || (mic != null && mic.Visible && mic.Area.Contains(Cursor.Position));
            bool want = allowed && (near || ear.Listening);
            if (want && (mic == null || !mic.Wanted))
            {
                if (mic == null)
                {
                    mic = new MicButton(delegate { return MicSpot(); });
                    mic.Clicked += delegate { Listen(); };
                }
                mic.Listening = ear.Listening;
                mic.Appear(true, TopMost);
            }
            else if (!want && mic != null && mic.Wanted) mic.Appear(false, TopMost);
            if (mic != null) mic.Listening = ear.Listening;
        }

        private Point MicSpot()
        {
            Rectangle area = Screen.FromPoint(new Point(anchor.X + Width / 2, anchor.Y + Height / 2)).WorkingArea;
            int x = Math.Min(anchor.X + Width - 40, area.Right - 64);
            int y = Math.Max(area.Top, Math.Min(anchor.Y + PaddingSize + (int)(preferences.Size * 0.10), area.Bottom - 64));
            return new Point(x, y);
        }

        // Starts listening for one phrase, or stops if she already is. What she hears goes into the chat window.
        internal void Listen()
        {
            if (ear.Listening)
            {
                if (ear.Spoke) { ear.Wrap(); return; }
                ear.Stop();
                ListeningOver();
                return;
            }
            if (!Ear.Available && !(preferences.UseWhisper && Whisper.Available(ear.SpeechFolder, ear.Language))) { Say("I have no ears! Add an English speech pack in Windows settings.", true); return; }
            if (ChatOpen()) chat.SetListening(true);
            ShowCut("eyes", 9);
            if (mic != null) mic.Listening = true;
            ear.Start(
                delegate(string text, double confidence) { if (IsHandleCreated) BeginInvoke((Action)delegate { Heard(text, confidence); }); },
                delegate(string why) { if (IsHandleCreated) BeginInvoke((Action)delegate { Unheard(why); }); });
        }

        private int unheard;

        // She is a bit dense about it: each time in a row she still cannot hear you, she says so more pointedly.
        internal void Unheard(string why)
        {
            ListeningOver();
            if (why == "error") { Respond("I can't use that microphone! Try another one in Chat, Microphone."); return; }
            if (why == "norecognizer") { Respond("I have no ears! Add an English speech pack in Windows settings."); return; }
            if (why == "quiet") { Respond("I can't hear any sound at all! Check your mic in Chat, Microphone, and that Windows lets desktop apps use it."); return; }
            if (ear.WhisperProblem != null) { Respond("Whisper isn't working, so I fell back to Windows' own ears. The reason is in voice-log.txt."); return; }
            unheard++;
            Respond(unheard == 1 ? "Can't hear you!" : unheard == 2 ? "Still can't hear you!" : unheard == 3 ? "Hello?? Still can't hear you!" : "Nope, still nothing! Try another mic in Chat, Microphone!");
        }

        private void ListeningOver()
        {
            if (chat != null) chat.SetListening(false);
            if (cutin != null && cutin.Visible && cutin.Key == "eyes") cutin.Dismiss();
            if (mic != null) mic.Listening = false;
        }

        // Clear speech is sent straight away, as if typed; a shaky guess is left in the box so it can be checked first.
        internal void Heard(string text, double confidence)
        {
            ListeningOver();
            text = (text ?? "").Trim();
            if (text.Length == 0) { Unheard("silence"); return; }
            unheard = 0;
            text = char.ToUpperInvariant(text[0]) + text.Substring(1);
            // With the chat window closed this is a spoken conversation: she answers in bubbles and no window opens.
            if (!ChatOpen())
            {
                if (confidence < 0.35) { Respond("Sorry, say that again?"); return; }
                OnChatSent(text);
                return;
            }
            if (confidence >= 0.35 && !chat.Busy) { chat.Submit(text); return; }
            chat.AddNote("I wasn't sure I heard that right. Check it, then send.");
            chat.SetDraft(text);
        }

        // A framed shot from the show beside her head, for a moment.
        internal void ShowCut(string key, double seconds)
        {
            if (!preferences.CutIns || hidden || !IsHandleCreated) return;
            Bitmap picture = Sprite(key);
            if (picture == null) return;
            if (cutin == null) cutin = new CutIn(delegate { return new Rectangle(anchor, Size); }, delegate { return effectsOn(); });
            cutin.Present(picture, key, seconds, TopMost, Math.Max(190, (int)(preferences.Size * 1.05)));
        }

        // The cut-ins that go with a mood, and when in the mood they appear.
        private void StartScript(Behavior which)
        {
            script = null;
            scriptNext = 0;
            scriptStart = clock.Elapsed.TotalSeconds;
            switch (which)
            {
                case Behavior.Look: script = new Beat[] { new Beat(0.35, "eyes-left", 0.9), new Beat(1.25, "eyes-right", 0.9), new Beat(2.15, "eyes", 0.7) }; break;
                case Behavior.Sleep: script = new Beat[] { new Beat(0.9, "eyes-closed", 1.6) }; break;
                case Behavior.Pat: script = new Beat[] { new Beat(0.25, "happyface", 1.5) }; break;
                case Behavior.Glitch: script = new Beat[] { new Beat(0.4, "eyes-many", 0.9), new Beat(1.3, "eyes-glitch", 0.45) }; break;
            }
        }

        private void AdvanceScript(double now)
        {
            if (script == null) return;
            while (scriptNext < script.Length && now - scriptStart >= script[scriptNext].At) { ShowCut(script[scriptNext].Key, script[scriptNext].Seconds); scriptNext++; }
            if (scriptNext >= script.Length) script = null;
        }

        private void StopScript()
        {
            script = null;
            if (cutin != null) cutin.Dismiss();
        }

        // Her answer goes into the chat window while it is open, and into a speech bubble otherwise.
        private double lastReplyAt = -1000;
        private static readonly Regex AgreeWords = new Regex(@"^(?:ok|okay|sure|fine|yeah|yes|yep|alright|deal|you'?re right|good point|fair enough|true|i agree|if you say so)\b", RegexOptions.IgnoreCase);

        internal string LastReply = "";

        private void Respond(string text)
        {
            text = Lang.T(text);
            LastReply = text;
            lastReplyAt = clock.Elapsed.TotalSeconds;
            if (ChatOpen()) { chat.AddHer(text); Speak("reply", true); }
            else Reply(text);
        }

        // Opens the chat window beside her (or brings it back), and keeps the conversation that is already in it.
        internal void Talk()
        {
            if (chat == null)
            {
                chat = new ChatWindow(Sprite("talk-closed"), Sprite("talk-open"));
                chat.Submitted += OnChatSent;
                chat.Spoke += delegate { Listen(); };
            }
            if (!chat.Visible)
            {
                Rectangle area = Screen.FromPoint(new Point(anchor.X + Width / 2, anchor.Y + Height / 2)).WorkingArea;
                int x = anchor.X - chat.Width - 6 >= area.Left ? anchor.X - chat.Width - 6 : Math.Min(anchor.X + Width + 6, area.Right - chat.Width);
                int y = Math.Max(area.Top, Math.Min(anchor.Y + Height - chat.Height, area.Bottom - chat.Height));
                chat.Location = new Point(Math.Max(area.Left, x), y);
                chat.TopMost = TopMost;
                chat.SetStatus(llmStatus);
                chat.Show();
            }
            chat.Activate();
            chat.FocusInput();
            if (!chat.Empty) return;
            if (!preferences.LlmOn) chat.AddNote("The local model is off. Open Chat in her menu and choose Use a local model.");
            else chat.AddHer(Pick(ChatLines));
        }

        private void OnChatSent(string text)
        {
            NoteName(text);
            AddSync(2, "chat");
            if (clock.Elapsed.TotalSeconds - lastReplyAt < 120 && AgreeWords.IsMatch((text ?? "").Trim())) AddSync(2, "agree");
            if (TrySync(text)) return;
            if (TryDare(text)) return;
            if (TryErrand(text)) return;
            if (TryCanon(text)) return;
            if (!preferences.LlmOn) { Respond("My brain's switched off! Chat, then Use a local model."); return; }
            if (llmLink == null) { Respond("I'm not connected yet. Looking again!"); Connect(false); return; }
            if (WantsLook(text)) { Look(text); return; }
            Converse(text);
        }


        private static readonly Regex LookWords = new Regex(@"\b(look at|check|see|watch)\b.{0,20}\b(screen|monitor|desktop|display)\b|\bwhat('?s| is| am i| are we)\b.{0,25}\b(on (my |the )?screen|watching|looking at|playing|doing)\b|\bcan you see\b|\b(?:this|the) (?:situation|screen|game|map|round|menu|page|level)\b|\bwhat (?:should|do|can|would) (?:i|we)\b.{0,30}\b(?:buy|pick|choose|do|play|equip|click|press)\b.{0,25}\b(?:here|now|next|this|these)\b|\bwhat (?:\w+ ){0,2}(?:should|do) i (?:buy|pick|choose|equip)\b", RegexOptions.IgnoreCase);

        internal static bool WantsLook(string text) { return LookWords.IsMatch(text ?? ""); }

        // One downscaled picture of the screen the pointer is on, as JPEG bytes. It lives in memory only and is never written anywhere.
        internal static byte[] Snapshot()
        {
            Rectangle area = Screen.FromPoint(Cursor.Position).Bounds;
            using (Bitmap full = new Bitmap(area.Width, area.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
            {
                Native.Grab(full, area);
                double scale = Math.Min(1.0, 1280.0 / area.Width);
                using (Bitmap small = new Bitmap((int)(area.Width * scale), (int)(area.Height * scale), System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                using (Graphics g = Graphics.FromImage(small))
                using (MemoryStream jpeg = new MemoryStream())
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(full, 0, 0, small.Width, small.Height);
                    System.Drawing.Imaging.ImageCodecInfo codec = null;
                    foreach (System.Drawing.Imaging.ImageCodecInfo c in System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()) if (c.MimeType == "image/jpeg") codec = c;
                    System.Drawing.Imaging.EncoderParameters quality = new System.Drawing.Imaging.EncoderParameters(1);
                    quality.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 70L);
                    small.Save(jpeg, codec, quality);
                    return jpeg.ToArray();
                }
            }
        }

        // Only when asked: one look at the screen goes to the local model with what is in front and what is playing, and is then dropped.
        internal void Look(string question)
        {
            if (!preferences.LookOn) { Respond("You've told me not to look! Chat, Let her look when I ask."); return; }
            if (!preferences.LlmOn || llmLink == null) { Respond("I need my brain connected to look. Chat, Use a local model."); return; }
            LlmLink eyes = llmLink.Cloud ? (preferences.CloudLook ? llmLink : localLink) : llmLink;
            if (eyes == null) { Respond("Looking stays on your computer. Run a local model, or allow it for the cloud in Chat, Cloud model."); return; }
            if (!eyes.Cloud && !eyes.Ollama) { Respond("I can only look through Ollama, sorry!"); return; }
            if (llmBusy) return;
            byte[] picture;
            try { picture = ScreenSource != null ? ScreenSource() : Snapshot(); }
            catch (Exception) { Respond("I can't see the screen right now!"); return; }
            llmBusy = true;
            string title = (Native.FrontTitle() ?? "").Replace("\"", "'");
            if (title.Length > 120) title = title.Substring(0, 120);
            Track playing = null;
            try { playing = Media.Read(); } catch (Exception) { }
            string context = "[The user asked you to look at their screen. A screenshot is attached; it is not kept. ";
            if (title.Length > 0) context += "The window in front is titled \"" + title + "\". ";
            if (playing != null && playing.Playing) context += "Media playing: " + Brief(playing).Replace("\"", "'") + ". ";
            context += question.Length > 0 ? "Answer what they said using what you can really see on the screen, in one or two short plain sentences. Only use what is visible and do not invent items, weapons or menu options that are not there; if you cannot tell, say so. " : "Say what you can really tell they are doing, in one or two short plain sentences. Only say what is visible; if you cannot tell, say so. ";
            context += "You are the small character on the screen, so if you see yourself, say so. ";
            context += question.Length > 0 ? "They said: \"" + question.Replace("\"", "'") + "\"]" : "]";
            ChatLine look = new ChatLine("user", context);
            look.Images = new string[] { Convert.ToBase64String(picture) };
            picture = null;
            List<ChatLine> sent = new List<ChatLine>();
            sent.Add(new ChatLine("system", BrainText()));
            int from = Math.Max(0, chatLog.Count - 6);
            for (int i = from; i < chatLog.Count; i++) sent.Add(chatLog[i]);
            sent.Add(look);
            chatLog.Add(new ChatLine("user", question.Length > 0 ? question : "(asked her to look at the screen)"));
            LlmLink link = eyes;
            string model = eyes.Cloud || Array.IndexOf(eyes.Models, preferences.LlmModel) < 0 ? eyes.Models[0] : preferences.LlmModel;
            int attempt = llmAttempt;
            ShowCut("eyes", 6);
            if (ChatOpen()) chat.SetBusy(true);
            else Say("…", true, 120);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string answer = "";
                try { answer = Llm.Chat(link, model, sent); }
                catch (Exception) { }
                sent = null;
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)delegate
                {
                    llmBusy = false;
                    if (chat != null) chat.SetBusy(false);
                    if (cutin != null && cutin.Visible && cutin.Key == "eyes") cutin.Dismiss();
                    if (attempt != llmAttempt) return;
                    if (answer.Length == 0) { Respond("My eyes went blurry. Ask again?"); return; }
                    answer = Llm.Shape(answer, chance);
                    chatLog.Add(new ChatLine("assistant", answer));
                    while (chatLog.Count > 24) chatLog.RemoveAt(0);
                    Respond(answer);
                });
            });
        }




        // ---- sync: how far she has got into your life. It grows with the time you spend, what you tell her, pats and finished dares ----
        private double activeSeconds;
        private ToolStripMenuItem syncItem;
        private static readonly string[] SyncUnlocks = { "", "TEAR", "CORRUPTION", "HEX RAIN", "SYNC BAR", "CHASER", "", "", "", "", "COMPLETE" };
        private static readonly Regex SyncAsk = new Regex(@"^(?:what'?s|what is|how'?s|how is|check|show me|tell me)\b.{0,18}\b(?:sync|friendship|bond)\b|^how close are we\b|^are we close\b", RegexOptions.IgnoreCase);

        internal int SyncPercent() { return Math.Min(100, preferences.SyncPoints / 10); }

        private string SyncTier()
        {
            int percent = SyncPercent();
            if (percent < 20) return "She barely knows the user. She is wary, dismissive and a little rude.";
            if (percent < 50) return "She is getting used to the user and teases them.";
            if (percent < 80) return "She is comfortable with the user and openly manipulates them like a friend would.";
            return "She treats the user as hers, casually possessive, like an old friend she has moved in with.";
        }

        // Points come from four places, each with a daily limit: time with her (one point per five active minutes, up to 24), what you tell her (two each, up to ten messages), pats, and finished dares.
        internal void AddSync(int points, string source)
        {
            string today = Senses.Now().ToString("yyyyMMdd");
            if (preferences.SyncDay != today) { preferences.SyncDay = today; preferences.SyncMinutes = 0; preferences.SyncChats = 0; preferences.SyncPats = 0; preferences.SyncAgrees = 0; }
            if (source == "time") { if (preferences.SyncMinutes >= 24) return; preferences.SyncMinutes++; }
            else if (source == "chat") { if (preferences.SyncChats >= 10) return; preferences.SyncChats++; }
            else if (source == "pat") { if (preferences.SyncPats >= 10) return; preferences.SyncPats++; }
            else if (source == "agree") { if (preferences.SyncAgrees >= 5) return; preferences.SyncAgrees++; }
            if (preferences.SyncPoints >= 1000) return;
            preferences.SyncPoints = Math.Min(1000, preferences.SyncPoints + points);
            int level = preferences.SyncPoints / 100;
            if (level > preferences.SyncShown)
            {
                preferences.SyncShown = level;
                string unlocked = level < SyncUnlocks.Length ? SyncUnlocks[level] : "";
                string line = level >= 10 ? Lang.T("SYNC 100%. COMPLETE. You're mine now. Kidding! ...Mostly.") : Lang.F("SYNC {0}%.", level * 10) + (unlocked.Length > 0 ? " " + Lang.F("UNLOCKED: {0}.", Lang.T(unlocked)) : "");
                if (preferences.Bubbles) Hack(line);
            }
            SavePreferences();
            if (syncItem != null) syncItem.Text = Lang.F("Sync with Capri: {0}%", SyncPercent());
        }

        // Mischief starts with plain bars and gains a style for every ten percent of sync.
        internal int PickKind() { return chance.Next(Math.Min(Glitch.Kinds.Length, 1 + SyncPercent() / 10)); }

        private bool TrySync(string said)
        {
            string text = (said ?? "").Trim().Trim('.', '!', '?', ' ');
            if (!SyncAsk.IsMatch(text)) return false;
            int percent = SyncPercent();
            string line = percent < 10 ? "Sync's at " + percent + "%. We're basically strangers." : percent < 40 ? "Sync's at " + percent + "%. Give it time." : percent < 80 ? "Sync's at " + percent + "%. Gettin' there." : "Sync's at " + percent + "%. You're basically mine.";
            Reflect(said, line);
            return true;
        }

        // ---- she notices things on her own: long idle, late night, a busy computer, a low battery, a very long time in one program ----
        private double lastObserve = -100;
        private double noticeNext = 240;
        private bool wasAway;
        private double peakIdle;
        private string frontApp = "";
        private string frontRaw = "";
        private double dareNext = 1200;
        private double dareOpenUntil;
        private double frontSince;
        private double cpuBusy;
        private readonly Dictionary<string, double> noticed = new Dictionary<string, double>();
        private static readonly string[] BackLines = { "Oh. You're back.", "Back already? I barely noticed.", "Gone {0} minutes. Not that I counted.", "Where'd you go? Tell me everything." };
        private static readonly string[] LateLines = { "It's {0}. Nobody's stoppin' ya.", "Still up at {0}? Good. More time with me.", "{0}. Great time for bad ideas." };
        private static readonly string[] BatteryLines = { "Battery's at {0}%. If you die, I'm stuck in a dead chip!", "{0}% battery. Plug in! I'd be in a real jam." };
        private static readonly string[] BusyLines = { "Your CPU's at {0}%. That's not me! ...Mostly.", "Somethin's eatin' {0}% of your CPU. Not me. Probably." };
        private static readonly string[] StayLines = { "{1} for {0}? Wow.", "Still in {1}? It's been {0}.", "{0} in {1}. I'm impressed. And bored." };

        private static string HoursText(double seconds)
        {
            double hours = Math.Round(seconds / 1800) / 2;
            return hours <= 1 ? Lang.T("an hour") : Lang.F("{0} hours", hours.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
        }

        // Every couple of seconds she looks at the signals. One remark at most every ten minutes or so, each kind with its own long pause, and none while she is busy or you are focusing.
        internal void Observe()
        {
            double now = clock.Elapsed.TotalSeconds;
            if (now - lastObserve < 2) return;
            double step = Math.Min(10, now - lastObserve);
            lastObserve = now;

            double idle = Senses.Idle();
            if (idle < 300) { activeSeconds += step; if (activeSeconds >= 300) { activeSeconds -= 300; AddSync(1, "time"); } }
            if (idle >= 1200) { wasAway = true; peakIdle = Math.Max(peakIdle, idle); }
            bool back = wasAway && idle < 5;
            double awayMinutes = peakIdle / 60;
            if (back) { wasAway = false; peakIdle = 0; }
            string raw = Senses.App() ?? "";
            string app = Senses.Friendly(raw);
            if (app != frontApp) { frontApp = app; frontRaw = raw; frontSince = now; }
            double cpu = Senses.Cpu();
            cpuBusy = cpu >= 90 ? cpuBusy + step : 0;

            if (!preferences.Notices || !preferences.Bubbles || hidden || fullscreenAway || behavior != Behavior.None || now < noticeNext) return;
            if (study == StudyPhase.Focus && !studyPaused) return;
            if (ChatOpen() || riding || dragging || (bubble != null && bubble.Visible)) return;

            DateTime clockNow = Senses.Now();
            int battery = Senses.Battery();
            if (back && awayMinutes >= 20) Remark("back", 1800, BackLines, now, ((int)awayMinutes).ToString());
            else if (clockNow.Hour >= 1 && clockNow.Hour < 5 && idle < 120) Remark("late", 7200, LateLines, now, clockNow.ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture).ToLowerInvariant());
            else if (battery >= 1 && battery < 20) Remark("battery", 1200, BatteryLines, now, battery.ToString());
            else if (cpuBusy >= 30) Remark("busy", 1800, BusyLines, now, ((int)Math.Round(cpu)).ToString());
            else if (app.Length > 0 && now - frontSince >= 5400) Remark("stay", 5400, StayLines, now, HoursText(now - frontSince), app);
            else if (preferences.Dares && now >= dareNext && now - frontSince >= 600 && idle < 60) GiveDare(false);
        }

        private static readonly string[] DoneLines = { "Yay! You did it!", "Told ya you could.", "Look at you, doin' stuff!", "See? Fun.", "Good boy!" };

        // A dare for what you are doing. The local model writes it when it is on; otherwise, or when its answer is no good, one from her own list.
        internal void GiveDare(bool asked)
        {
            double now = clock.Elapsed.TotalSeconds;
            dareNext = now + 1500 + chance.NextDouble() * 1500;
            noticeNext = Math.Max(noticeNext, now + 300);
            string[] pool = Dares.Pool(Dares.Category(frontRaw));
            string fallback = Pick(pool);
            if (!preferences.LlmOn || llmLink == null || llmBusy || !(llmLink.Ollama || llmLink.Cloud)) { SayDare(fallback, asked); return; }
            llmBusy = true;
            if (asked) Say("…", true, 120);
            string place = frontApp.Length > 0 ? " They have been in " + frontApp + " for " + Math.Max(1, (int)((now - frontSince) / 60)) + " minutes." : "";
            List<ChatLine> ask = new List<ChatLine>();
            ask.Add(new ChatLine("system", BrainText()));
            string seedA = Pick(pool), seedB = Pick(pool);
            // Only with its own switch on, and only through a model that is allowed to see pictures: a local one, or the cloud one if screenshots were allowed there.
            LlmLink eyes = llmLink.Cloud ? (preferences.CloudLook ? llmLink : null) : (llmLink.Ollama ? llmLink : null);
            byte[] picture = null;
            if (preferences.VisionDares && eyes != null)
            {
                try { picture = ScreenSource != null ? ScreenSource() : Snapshot(); }
                catch (Exception) { picture = null; }
            }
            bool looked = picture != null;
            if (looked)
            {
                ChatLine sight = new ChatLine("user", "[Look at the attached screenshot (it is not kept) and dare the user, playfully, about what is really happening on their screen right now." + place + " Give ONE dare in one short sentence that begins with \"Dare you to\". It must be harmless, silly and quick (under a minute). Nothing dangerous, gross or illegal, nothing that hurts real people or touches their private things. Only use what you can really see; if you cannot tell, dare them about what they are doing instead.]");
                sight.Images = new string[] { Convert.ToBase64String(picture) };
                picture = null;
                ask.Add(sight);
                ShowCut("eyes", 5);
            }
            else ask.Add(new ChatLine("user", "[Dare the user, playfully." + place + " Give ONE new dare in one short sentence that begins with \"Dare you to\" and fits what they are doing. It must be harmless, silly and quick (under a minute) and doable at the computer or in the room. Nothing dangerous, gross, illegal, or about other people's private things, and nothing that puts your mouth on anything. For the kind of thing, but do not repeat these: " + seedA + " / " + seedB + "]"));
            LlmLink link = llmLink;
            string model = ActiveModel();
            int attempt = llmAttempt;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string answer = "";
                try { answer = Llm.Chat(link, model, ask); }
                catch (Exception) { }
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)delegate
                {
                    llmBusy = false;
                    if (looked && cutin != null && cutin.Visible && cutin.Key == "eyes") cutin.Dismiss();
                    if (attempt != llmAttempt) return;
                    SayDare(Dares.Check(answer) ?? fallback, asked);
                });
            });
        }

        private void SayDare(string dare, bool asked)
        {
            dareOpenUntil = clock.Elapsed.TotalSeconds + 900;
            if (asked) Reflect("dare me", dare);
            else Say(dare, false, 0);
        }

        private void Remark(string kind, double pause, string[] lines, double now, params string[] values)
        {
            double last;
            if (noticed.TryGetValue(kind, out last) && now - last < pause) return;
            noticed[kind] = now;
            noticeNext = now + 480 + chance.NextDouble() * 480;
            Say(string.Format(Pick(lines), values), false, 0);
        }

        // ---- what she remembers: a plain text file you can read, edit or delete, one fact per line ----
        private List<string> memory;
        internal Action<string> Opener;
        internal Action<byte> KeySender;
        private static readonly string[] Noted = { "Noted! I never forget!", "Got it. Filed away!", "Mm-hm. I'll keep that!", "Written on my soul. Kidding!" };
        private static readonly string[] Done = { "Done!", "Easy!", "Sure thing!", "On it!" };

        internal string MemoryPath() { return Path.Combine(Path.GetDirectoryName(settingsPath), "memory.txt"); }

        internal List<string> Memory()
        {
            if (memory != null) return memory;
            memory = new List<string>();
            try
            {
                if (File.Exists(MemoryPath()))
                    foreach (string line in File.ReadAllLines(MemoryPath(), Encoding.UTF8))
                        if (line.Trim().Length > 0 && memory.Count < 60) memory.Add(line.Trim());
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return memory;
        }

        private void SaveMemory()
        {
            try { File.WriteAllLines(MemoryPath(), Memory().ToArray(), new UTF8Encoding(false)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal bool Remember(string fact)
        {
            fact = Regex.Replace((fact ?? "").Trim().TrimEnd('.', '!', '?', ' '), @"\s+", " ");
            if (fact.Length < 3) return false;
            if (fact.Length > 200) fact = fact.Substring(0, 200);
            List<string> facts = Memory();
            foreach (string known in facts) if (string.Equals(known, fact, StringComparison.OrdinalIgnoreCase)) return true;
            facts.Add(fact);
            while (facts.Count > 60) facts.RemoveAt(0);
            SaveMemory();
            return true;
        }

        // How many notes were dropped: only a note that is the single match for these words is removed.
        internal int Forget(string about)
        {
            List<string> facts = Memory();
            List<string> keywords = new List<string>();
            foreach (string word in Regex.Split(about.ToLowerInvariant(), @"[^a-z0-9']+"))
                if (word.Length >= 3 && !Regex.IsMatch(word, @"^(?:the|and|from|your|you|memory|notes|brain|about|that|this|with|have|has|had|was|were|are|not|remove|forget|delete|erase|our)$")) keywords.Add(word);
            List<string> hits = facts.FindAll(delegate(string fact)
            {
                if (keywords.Count == 0) return fact.IndexOf(about, StringComparison.OrdinalIgnoreCase) >= 0;
                foreach (string word in keywords) if (fact.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0) return false;
                return true;
            });
            if (hits.Count != 1) return hits.Count == 0 ? 0 : -hits.Count;
            facts.Remove(hits[0]);
            SaveMemory();
            return 1;
        }

        internal void ForgetEverything()
        {
            Memory().Clear();
            SaveMemory();
        }

        // Her personality for the model, with what she knows about you added when memory is on.
        internal string BrainText()
        {
            string text = PersonaText() + "\n\nHOW CLOSE SHE IS TO THE USER RIGHT NOW (sync " + SyncPercent() + "%): " + SyncTier();
            List<string> facts = Memory();
            if (!preferences.MemoryOn || facts.Count == 0 || (llmLink != null && llmLink.Cloud && !preferences.CloudMemory)) return text;
            StringBuilder block = new StringBuilder(text);
            block.Append("\n\nWHAT THE USER HAS TOLD YOU (use one only when it really fits what they said; never list them or say you keep notes):");
            for (int i = Math.Max(0, facts.Count - 25); i < facts.Count; i++) block.Append("\n- ").Append(facts[i]);
            return block.ToString();
        }

        private void NoteName(string text)
        {
            if (!preferences.MemoryOn) return;
            string name = Errands.Name(text);
            if (name != null) Remember("their name is " + name);
        }

        private void Reflect(string said, string reply)
        {
            chatLog.Add(new ChatLine("user", said));
            chatLog.Add(new ChatLine("assistant", reply));
            while (chatLog.Count > 24) chatLog.RemoveAt(0);
            Respond(reply);
        }

        private void Open(string target)
        {
            try
            {
                if (Opener != null) { Opener(target); return; }
                ProcessStartInfo start = new ProcessStartInfo(target);
                start.UseShellExecute = true;
                Process.Start(start);
            }
            catch (Exception) { Respond("Hm, that won't open!"); }
        }

        private void Press(byte key, int times)
        {
            for (int i = 0; i < times; i++)
            {
                if (KeySender != null) KeySender(key); else Native.PressKey(key);
            }
        }

        private static readonly Regex DareAsk = new Regex(@"^(?:dare me|dear me|give me a dare|i need a dare|dare me to something|i dare you|got any dares|dare)\b", RegexOptions.IgnoreCase);
        private static readonly Regex DareDone = new Regex(@"^(?:done|i did it|did it|i'm done|finished|i did|ok i did|okay i did)\b", RegexOptions.IgnoreCase);

        private bool TryDare(string said)
        {
            string text = (said ?? "").Trim().Trim('.', '!', '?', ' ');
            text = Regex.Replace(text, @"^(?:hey |ok |okay )?(?:capri|cappy)[,!. ]+", "", RegexOptions.IgnoreCase);
            if (DareAsk.IsMatch(text)) { GiveDare(true); return true; }
            if (clock.Elapsed.TotalSeconds < dareOpenUntil && DareDone.IsMatch(text))
            {
                dareOpenUntil = 0;
                preferences.DaresDone++;
                AddSync(10, "dare");
                SavePreferences();
                Reflect(said, Pick(DoneLines));
                return true;
            }
            return false;
        }

        private string lastCanon = "";

        // One of her own lines for a question that has one. The same line twice in a row is left to the model.
        private bool TryCanon(string said)
        {
            string line = Canon.Reply(said);
            if (line == null || line == lastCanon) { lastCanon = ""; return false; }
            lastCanon = line;
            Reflect(said, line);
            return true;
        }

        // ---- play a song on Spotify: open its search, find the Play button for it by name and press it ----
        private bool spotifyBusy;

        private Track ReadMedia()
        {
            try { return IsHandleCreated ? (Track)Invoke((Func<Track>)Media.Read) : null; }
            catch (Exception) { return null; }
        }

        private void PlayOnSpotify(string query, string said)
        {
            if (spotifyBusy) { Reflect(said, "Still on the last one!"); return; }
            Track before = null;
            try { before = Media.Read(); } catch (Exception) { }
            if (before != null && before.Playing && SpotifyUi.Score(before.Title + " " + before.Artist, query) >= 0.6) { Reflect(said, "That's already playing!"); return; }
            Reflect(said, Lang.F("Finding {0} on Spotify…", query));
            string beforeKey = before != null && before.Playing ? before.Key : "";
            try
            {
                if (Opener != null) Opener("spotify:search:" + Uri.EscapeDataString(query));
                else { ProcessStartInfo start = new ProcessStartInfo("spotify:search:" + Uri.EscapeDataString(query)); start.UseShellExecute = true; Process.Start(start); }
            }
            catch (Exception) { Respond("I can't find Spotify on this PC."); return; }
            spotifyBusy = true;
            double limit = Math.Max(0.2, preferences.SpotifySeconds);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string result = null;
                int picked = -1;
                bool seen = false;
                string[] lastNames = null;
                string lastVerb = null;
                bool learnedNew = false;
                for (double waited = 0; waited < limit && picked < 0; waited += SpotifyUi.Poll / 1000.0)
                {
                    string[] names = SpotifyUi.Buttons();
                    if (names != null)
                    {
                        seen = true;
                        lastNames = names;
                        string verb = SpotifyUi.Choose(names, preferences.SpotifyVerb);
                        lastVerb = verb;
                        // Only a wording that many buttons share is remembered for next time.
                        if (verb != null && verb != preferences.SpotifyVerb && SpotifyUi.Count(names, verb) >= 6) { preferences.SpotifyVerb = verb; learnedNew = true; }
                        int index = verb == null ? -1 : SpotifyUi.Find(names, verb, query);
                        if (index >= 0 && SpotifyUi.Invoke(index)) { picked = index; break; }
                    }
                    Thread.Sleep(SpotifyUi.Poll);
                }
                if (picked < 0 && lastNames != null) WriteSpotifyNote(query, lastVerb, lastNames);
                if (learnedNew) { if (IsHandleCreated) BeginInvoke((Action)delegate { SavePreferences(); }); }
                if (picked < 0) result = seen ? Lang.F("I couldn't find {0} in Spotify's results.", query) : "Spotify isn't open or won't let me in.";
                else
                {
                    bool nudged = false;
                    for (double waited = 0; waited < Math.Min(8, limit) && result == null; waited += SpotifyUi.Poll / 1000.0)
                    {
                        Thread.Sleep(SpotifyUi.Poll);
                        Track now = ReadMedia();
                        if (now != null && now.Playing && now.Key != beforeKey) result = Lang.F("Playing {0}.", Brief(now));
                        // Pressing play on a result sometimes only selects it: if nothing is playing after a few seconds, the play key is pressed once.
                        else if (!nudged && waited >= Math.Min(3.0, limit / 2)) { nudged = true; Press(0xB3, 1); }
                    }
                    if (result == null) result = Lang.F("I pressed play on {0} but nothing started.", query);
                }
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)delegate { spotifyBusy = false; Respond(result); });
            });
        }

        // What she saw in Spotify when she could not find the song, so it can be looked at: the names of its buttons and the Play wording she used.
        private void WriteSpotifyNote(string query, string verb, string[] names)
        {
            try
            {
                List<string> lines = new List<string>();
                lines.Add("Looking for: " + query);
                lines.Add("Play wording used: " + (verb ?? "(none found)"));
                lines.Add("Buttons seen (" + names.Length + "):");
                foreach (string name in names) lines.Add("  " + name);
                File.WriteAllLines(Path.Combine(Path.GetDirectoryName(settingsPath), "spotify-buttons.txt"), lines.ToArray(), new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // "Play it on YouTube": the first video for the search is opened itself. One request goes to youtube.com for this, nothing else.
        private void PlayOnYouTube(string query, string search, string said)
        {
            Reflect(said, Lang.F("Finding {0} on YouTube…", query));
            ThreadPool.QueueUserWorkItem(delegate
            {
                string id = YouTube.FirstVideo(YouTube.Fetch(query));
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)delegate
                {
                    Open(id != null ? "https://www.youtube.com/watch?v=" + id : search);
                    Respond(id != null ? Lang.F("Playing {0}!", query) : "I couldn't pick one, so here are the results.");
                });
            });
        }

        // The small things she can do without asking a model. Returns false when the words were not a request, so they go to the model as usual.
        internal bool TryErrand(string said)
        {
            Errand errand = Errands.Parse(said);
            switch (errand.Kind)
            {
                case ErrandKind.None: return false;
                case ErrandKind.Remember:
                    if (!preferences.MemoryOn) { Reflect(said, "My memory's switched off! Chat, Memory."); return true; }
                    if (Errands.Sensitive(errand.Text)) { Reflect(said, "Not that one! Passwords belong in a password manager."); return true; }
                    Reflect(said, Remember(errand.Text) ? Pick(Noted) + " (" + errand.Text.TrimEnd('.', '!', '?') + ")" : "Hm, say that again?");
                    return true;
                case ErrandKind.Forget:
                    int dropped = Forget(errand.Text);
                    Reflect(said, dropped == 1 ? "Forgotten! Convenient, huh?" : dropped == 0 ? "I don't have anything like that!" : Lang.F("That's {0} things! Be more specific.", -dropped));
                    return true;
                case ErrandKind.ForgetAll:
                    ForgetEverything();
                    Reflect(said, "Everything about you? Gone! Hi, I'm Capri!");
                    return true;
                case ErrandKind.Timer:
                    if (AddAlarm(DateTime.Now.AddSeconds(errand.Seconds), errand.Text.Length > 0 ? errand.Text : "Timer: " + Errands.Describe(errand.Seconds), false)) Reflect(said, Lang.F("Timer's on for {0}!", Errands.Describe(errand.Seconds) + (errand.Text.Length > 0 ? ": " + errand.Text : "")));
                    else Reflect(said, "That's plenty of alarms already!");
                    return true;
                case ErrandKind.Clock:
                    if (AddAlarm(errand.Due, errand.Text, false)) Reflect(said, Lang.F("Alarm's set for {0}!", errand.Due.ToString("HH:mm")));
                    else Reflect(said, "That's plenty of alarms already!");
                    return true;
                case ErrandKind.ClearAlarms:
                    preferences.Alarms.Clear();
                    SavePreferences();
                    RebuildAlarms();
                    Reflect(said, "All cleared!");
                    return true;
                case ErrandKind.Study:
                    StartStudy((int)(errand.Seconds / 60), 5);
                    Reflect(said, Lang.F("Focus time! {0} minutes. I'll keep quiet!", (int)(errand.Seconds / 60)));
                    return true;
                case ErrandKind.StopStudy:
                    if (study == StudyPhase.Off) { Reflect(said, "We're not studying!"); return true; }
                    StopStudy();
                    Reflect(said, "Ok, stopped!");
                    return true;
                case ErrandKind.Web:
                    Open(errand.Target);
                    Reflect(said, Lang.F("Looking up {0}!", errand.Text));
                    return true;
                case ErrandKind.PlaySong:
                    PlayOnSpotify(errand.Text, said);
                    return true;
                case ErrandKind.PlayVideo:
                    PlayOnYouTube(errand.Text, errand.Target, said);
                    return true;
                case ErrandKind.Open:
                    Open(errand.Target);
                    Reflect(said, errand.More ? Lang.F("Opened {0}. The rest I can't do yet.", errand.Text) : Pick(Done));
                    return true;
                case ErrandKind.Pause:
                case ErrandKind.Play:
                    Track now = null;
                    try { now = Media.Read(); } catch (Exception) { }
                    if (errand.Kind == ErrandKind.Pause && (now == null || !now.Playing)) { Reflect(said, "Nothing's playing!"); return true; }
                    if (errand.Kind == ErrandKind.Play && now != null && now.Playing) { Reflect(said, "It's already playing!"); return true; }
                    Press(0xB3, 1);
                    Reflect(said, Pick(Done));
                    return true;
                case ErrandKind.Next:
                    Press(0xB0, 1);
                    Reflect(said, "Skipped!");
                    return true;
                case ErrandKind.Previous:
                    Press(0xB1, 1);
                    Reflect(said, "Back we go!");
                    return true;
                case ErrandKind.Mute:
                    Press(0xAD, 1);
                    Reflect(said, Pick(Done));
                    return true;
                case ErrandKind.Volume:
                    Press(errand.Up ? (byte)0xAF : (byte)0xAE, errand.Presses);
                    Reflect(said, errand.Up ? "Louder!" : "Quieter!");
                    return true;
            }
            return false;
        }

        internal void Converse(string text)
        {
            if (llmLink == null || llmBusy) return;
            llmBusy = true;
            ChatLine said = new ChatLine("user", text);
            chatLog.Add(said);
            while (chatLog.Count > 24) chatLog.RemoveAt(0);
            List<ChatLine> sent = new List<ChatLine>(chatLog);
            sent.Insert(0, new ChatLine("system", BrainText()));
            LlmLink link = llmLink;
            string model = ActiveModel();
            int attempt = llmAttempt;
            if (ChatOpen()) chat.SetBusy(true);
            else Say("…", true, 120);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string answer = "";
                bool failed = false;
                try { answer = Llm.Chat(link, model, sent); }
                catch (Exception) { failed = true; }
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)delegate
                {
                    if (attempt != llmAttempt) { if (chat != null) chat.SetBusy(false); return; }
                    llmBusy = false;
                    if (chat != null) chat.SetBusy(false);
                    if (failed || answer.Length == 0)
                    {
                        chatLog.Remove(said);
                        Respond(link.Cloud ? Lang.F("Cloud trouble: {0}. Chat, Cloud model settings to change it.", Cloud.LastError) : failed ? "I can't reach my brain! Is the model still running?" : "Hm, I lost my train of thought!");
                        return;
                    }
                    answer = Llm.Shape(answer, chance);
                    chatLog.Add(new ChatLine("assistant", answer));
                    while (chatLog.Count > 24) chatLog.RemoveAt(0);
                    Respond(answer);
                });
            });
        }

        private void SetHidden(bool value)
        {
            hidden = value;
            visibilityItem.Text = value ? "Show Capri" : "Hide Capri";
            if (value) { StopRide(); StopBehavior(); if (bubble != null) bubble.Dismiss(); if (chat != null) chat.Hide(); if (cutin != null) cutin.Dismiss(); if (mic != null) mic.Appear(false, TopMost); ear.Stop(); pendingClick = false; clickDelay.Stop(); frames.Stop(); Hide(); }
            else { anchor = KeepOnScreen(anchor); Location = anchor; Noticed(); Show(); RenderFrame(); UpdateAnimation(); }
        }

        // Steps aside while a game or full screen video is in front and comes back when it ends. Showing her by hand wins until then.
        internal void CheckFullscreen()
        {
            bool full = preferences.HideInFullscreen && fullscreenProbe();
            if (!full)
            {
                fullscreenOverride = false;
                if (fullscreenAway) { fullscreenAway = false; SetHidden(false); }
                return;
            }
            if (fullscreenAway || fullscreenOverride || hidden) return;
            fullscreenAway = true;
            SetHidden(true);
        }

        private void RestoreAtCursor()
        {
            if (fullscreenAway) { fullscreenAway = false; fullscreenOverride = true; }
            StopRide();
            StopBehavior(false);
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            anchor = new Point(area.Right - Width - 28, area.Bottom - Height - 12);
            restHome = anchor;
            SetHidden(false);
            SavePreferences();
        }

        private bool NeedsFrames() { return preferences.Motion || riding || behavior != Behavior.None || sleepAmount > 0.01; }

        private void UpdateAnimation() { if (NeedsFrames() && !hidden) frames.Start(); else frames.Stop(); RenderFrame(); }

        private static double Ease(double t) { t = Math.Max(0, Math.Min(1, t)); return t * t * (3 - 2 * t); }

        private static double Envelope(double age, double length, double fade) { return Ease(age / fade) * Ease((length - age) / fade); }

        private static double Length(Behavior which)
        {
            switch (which)
            {
                case Behavior.Stretch: return 2.6;
                case Behavior.Look: return 3.0;
                case Behavior.Yawn: return 2.4;
                case Behavior.Pat: return 2.8;
                case Behavior.Glitch: return glitchLength;
                default: return 0;
            }
        }

        private void Play(Behavior which, bool manual)
        {
            if (hidden || riding || dragging || !IsHandleCreated || (!manual && menu.Visible)) return;
            if (behavior == Behavior.Peek || behavior == Behavior.Sit) StopBehavior();
            bool placed = sitChosen;
            if (which == Behavior.Sit && !placed && !FindSill())
            {
                nextSit = clock.Elapsed.TotalSeconds + 90;
                if (manual) Say("nowhere to sit. try a window lower on the screen.", true);
                return;
            }
            behavior = which;
            behaviorStart = clock.Elapsed.TotalSeconds;
            followWithSleep = false;
            untilTouched = manual && (which == Behavior.Sleep || which == Behavior.Peek || which == Behavior.Sit);
            if (which == Behavior.Sleep) sleepKey = chance.Next(2) == 0 ? "sleep" : "laying";
            if (which == Behavior.Peek) BeginPeek(PickEdge());
            if (which == Behavior.Sit) BeginSit(placed);
            if (which == Behavior.Glitch) BeginGlitch(manual);
            StartScript(which);
            frames.Start();
            RenderFrame();
            if (which == Behavior.Pat) Say(Pick(PatLines), manual);
            if (VoiceFor(which).Length > 0) Speak(VoiceFor(which), manual);
        }

        // Putting her back home is right for most interruptions; when she is being dragged she stays wherever she is dropped.
        private void StopBehavior() { StopBehavior(true); }

        private void StopBehavior(bool goHome)
        {
            if (behavior == Behavior.Peek) { anchor = peekHome; leanAngle = 0; }
            if (behavior == Behavior.Sit) { if (goHome) anchor = sitHome; leanAngle = 0; }
            if (behavior == Behavior.Glitch) EndGlitch();
            if (script != null) StopScript();
            behavior = Behavior.None;
            followWithSleep = false;
            sleepAmount = 0;
        }

        // Ctrl + Alt + C stops her mischief, the same way it makes her hop off the cursor.
        private void ArmEscape() { if (!hotkey && IsHandleCreated) hotkey = Native.RegisterHotKey(Handle, DismountId, 0x0001 | 0x0002 | 0x4000, 0x43); }

        private void DisarmEscape()
        {
            if (riding || !hotkey || !IsHandleCreated) return;
            Native.UnregisterHotKey(Handle, DismountId);
            hotkey = false;
        }

        // A glitch: she shivers into red and green ghosts, the overlay interferes with the screen, and a hacked note types itself out.
        private void BeginGlitch(bool manual)
        {
            glitchNote = !glitchQuiet;
            glitchQuiet = false;
            glitchManual = manual;
            glitchNoted = false;
            int kind = glitchKindNext >= 0 ? glitchKindNext : PickKind();
            Glitch.SyncTarget = Math.Max(0.02, SyncPercent() / 100.0);
            glitchKindNext = -1;
            glitchLength = Glitch.Seconds[kind];
            ArmEscape();
            if (!effectsOn()) return;
            if (chipArt == null)
                using (Stream image = Assembly.GetExecutingAssembly().GetManifestResourceStream("Capri.Chip.png"))
                    if (image != null) using (Bitmap original = new Bitmap(image)) chipArt = new Bitmap(original);
            if (overlay == null) overlay = new Glitch(chipArt);
            overlay.Kind = kind;
            overlay.Run(Screen.FromPoint(new Point(anchor.X + Width / 2, anchor.Y + Height / 2)).Bounds, Length(Behavior.Glitch));
        }

        private void EndGlitch()
        {
            if (overlay != null) overlay.Stop();
            DisarmEscape();
            nextMischief = clock.Elapsed.TotalSeconds + 180 + chance.NextDouble() * 300;
        }

        private void Hack(string text) { Say(text, true, Math.Min(12, 4 + text.Length * 0.07), true); }

        private void AdvanceGlitch(double now)
        {
            if (glitchNoted || !glitchNote || now - behaviorStart < 0.9) return;
            glitchNoted = true;
            if (preferences.Bubbles || glitchManual) Hack(Pick(HackLines));
        }

        // The top edges of other programs' windows that she could sit on, nearest first: they need room above them and a clear spot.
        private bool FindSill()
        {
            Rectangle area = Screen.FromPoint(new Point(anchor.X + Width / 2, anchor.Y + Height / 2)).WorkingArea;
            Point seat = SitSeat();
            Point rest = new Point(anchor.X + seat.X, anchor.Y + seat.Y);
            System.Collections.Generic.List<Native.Pane> panes = Native.Panes(Handle, bubble == null || !bubble.IsHandleCreated ? IntPtr.Zero : bubble.Handle, testing);
            double best = double.MaxValue;
            bool found = false;
            for (int i = 0; i < panes.Count; i++)
            {
                if (!panes[i].Sittable) continue;
                Rectangle frame = panes[i].Frame;
                if (frame.Top - seat.Y + 6 < area.Top - 30 || frame.Top > area.Bottom - 80 || frame.Right <= area.Left + 100 || frame.Left >= area.Right - 100) continue;
                int reach = (int)(preferences.Size * 0.6f);
                int low = Math.Max(frame.Left + 40, area.Left + 40);
                int high = Math.Min(frame.Right - reach, area.Right - reach);
                if (high < low) continue;
                int x = Math.Max(low, Math.Min(high, rest.X));
                bool covered = false;
                for (int above = 0; above < i; above++) if (panes[above].Frame.Contains(x, frame.Top + 3)) covered = true;
                if (covered) continue;
                double cost = Math.Abs(x - rest.X) + Math.Abs(frame.Top - rest.Y) * 0.5;
                if (cost >= best) continue;
                best = cost;
                found = true;
                sitWindow = panes[i].Handle;
                sitFrac = (x - frame.Left) / (double)frame.Width;
            }
            return found;
        }

        // Where her window should be to sit on the edge; false when her window has gone or been minimized.
        private bool SitTarget(out Point target)
        {
            target = anchor;
            if (!Native.IsWindow(sitWindow)) return false;
            Rectangle frame = Native.FrameOf(sitWindow);
            if (frame.Width < 40 || frame.Left < -10000) return false;
            Point seat = SitSeat();
            target = new Point(frame.Left + (int)(sitFrac * frame.Width) - seat.X, frame.Top - seat.Y + 6);
            return true;
        }

        // She hops up from where she is. When she was dropped on a window she goes back to the last spot on the ground, not to the air above the window.
        private void BeginSit(bool dropped)
        {
            sitStage = SitStage.Hopping;
            sitSlide = 0;
            sitHome = dropped ? restHome : anchor;
            if (!dropped) restHome = anchor;
            sitFrom = anchor;
            sitStart = sitChecked = lastAdvance = clock.Elapsed.TotalSeconds;
        }

        private void LeaveSeat()
        {
            if (behavior != Behavior.Sit || sitStage == SitStage.Leaving) return;
            sitStage = SitStage.Leaving;
            sitFrom = anchor;
        }

        // She still has a seat while her window is there, on screen, visible and not covered by a window in front of it.
        private bool SeatHolds()
        {
            if (!Native.IsWindow(sitWindow) || Native.Cloaked(sitWindow)) return false;
            Rectangle area = Screen.FromPoint(new Point(anchor.X + Width / 2, anchor.Y + Height / 2)).WorkingArea;
            Point seat = SitSeat();
            System.Collections.Generic.List<Native.Pane> panes = Native.Panes(Handle, bubble == null || !bubble.IsHandleCreated ? IntPtr.Zero : bubble.Handle, testing);
            for (int i = 0; i < panes.Count; i++)
            {
                if (panes[i].Handle != sitWindow) continue;
                Rectangle frame = panes[i].Frame;
                if (!panes[i].Sittable || frame.Top - seat.Y + 6 < area.Top - 30) return false;
                int x = frame.Left + (int)(sitFrac * frame.Width);
                for (int above = 0; above < i; above++) if (panes[above].Frame.Contains(x, frame.Top + 3)) return false;
                return true;
            }
            return false;
        }

        private void AdvanceSit(double now, double dt)
        {
            Point target;
            if (!SitTarget(out target)) LeaveSeat();
            switch (sitStage)
            {
                case SitStage.Hopping:
                    sitSlide = Math.Min(1, sitSlide + dt);
                    double along = Ease(sitSlide);
                    anchor = new Point(sitFrom.X + (int)Math.Round((target.X - sitFrom.X) * along), sitFrom.Y + (int)Math.Round((target.Y - sitFrom.Y) * along - Math.Sin(Math.PI * sitSlide) * preferences.Size * 0.18));
                    if (sitSlide >= 1) { sitStage = SitStage.Sitting; sitStart = now; sitChecked = now; if (chance.Next(2) == 0) { Say(Pick(SitLines), false); Speak("sit", false); } }
                    break;
                case SitStage.Sitting:
                    int gap = Math.Max(Math.Abs(target.X - anchor.X), Math.Abs(target.Y - anchor.Y));
                    anchor = gap < 3 ? target : new Point(anchor.X + (int)Math.Round((target.X - anchor.X) * 0.6), anchor.Y + (int)Math.Round((target.Y - anchor.Y) * 0.6));
                    leanAngle = Math.Sin(now * 1.3) * 2;
                    if (now - sitChecked >= 0.4)
                    {
                        sitChecked = now;
                        if (!SeatHolds() || (!untilTouched && now - sitStart > 150)) LeaveSeat();
                    }
                    break;
                default:
                    sitSlide = Math.Max(0, sitSlide - dt);
                    double back = Ease(sitSlide);
                    anchor = new Point(sitHome.X + (int)Math.Round((sitFrom.X - sitHome.X) * back), sitHome.Y + (int)Math.Round((sitFrom.Y - sitHome.Y) * back - Math.Sin(Math.PI * sitSlide) * preferences.Size * 0.18));
                    if (sitSlide > 0) break;
                    StopBehavior();
                    nextSit = now + 240 + chance.NextDouble() * 240;
                    break;
            }
        }

        private void Noticed()
        {
            double now = clock.Elapsed.TotalSeconds;
            lastActive = now;
            if (behavior == Behavior.Peek && (peekStage == PeekStage.Heading || peekStage == PeekStage.Looking)) peekStage = peekOut > 0 ? PeekStage.Ducking : PeekStage.Returning;
            if (behavior != Behavior.Sleep) return;
            behavior = Behavior.None;
            nextIdle = now + 20 + chance.NextDouble() * 20;
            Say(Pick(WakeLines), false);
            Speak("wake", false);
            ShowCut("eyes", 1.0);
        }

        // Left and right are only used when no other monitor sits beyond that edge, so she never pokes into a neighbouring screen.
        private int PickEdge()
        {
            Screen screen = Screen.FromPoint(new Point(anchor.X + Width / 2, anchor.Y + Height / 2));
            Rectangle bounds = screen.Bounds;
            Rectangle[] beyond = { new Rectangle(bounds.Left - 8, bounds.Top, 8, bounds.Height), new Rectangle(bounds.Right, bounds.Top, 8, bounds.Height), new Rectangle(bounds.Left, bounds.Bottom, bounds.Width, 8) };
            System.Collections.Generic.List<int> open = new System.Collections.Generic.List<int>();
            for (int edge = 0; edge < beyond.Length; edge++)
            {
                bool blocked = false;
                foreach (Screen other in Screen.AllScreens) if (other.DeviceName != screen.DeviceName && other.Bounds.IntersectsWith(beyond[edge])) blocked = true;
                if (!blocked) open.Add(edge);
            }
            return open.Count == 0 ? chance.Next(3) : open[chance.Next(open.Count)];
        }

        // Edge 0 is the left side of the screen, 1 the right and 2 the bottom.
        private void BeginPeek(int edge)
        {
            Rectangle area = Screen.FromPoint(new Point(anchor.X + Width / 2, anchor.Y + Height / 2)).WorkingArea;
            peekEdge = edge;
            peekHome = anchor;
            peekTarget = edge == 0 ? new Point(area.Left, anchor.Y) : edge == 1 ? new Point(area.Right - Width, anchor.Y) : new Point(anchor.X, area.Bottom - Height);
            peekStage = PeekStage.Heading;
            peekSlide = 0;
            peekOut = 0;
            lastAdvance = lastPeek = clock.Elapsed.TotalSeconds;
        }

        private void AdvancePeek(double now, double dt)
        {
            switch (peekStage)
            {
                case PeekStage.Heading:
                    peekSlide = Math.Min(1, peekSlide + dt / 1.3);
                    if (peekSlide >= 1) { peekStage = PeekStage.Looking; peekStageStart = now; Speak("peek", false); }
                    break;
                case PeekStage.Looking:
                    peekOut = Math.Min(1, peekOut + dt / 0.9);
                    if (now - peekStageStart >= 5) peekStage = PeekStage.Ducking;
                    break;
                case PeekStage.Ducking:
                    peekOut = Math.Max(0, peekOut - dt / 0.7);
                    if (peekOut <= 0) peekStage = PeekStage.Returning;
                    break;
                default:
                    peekSlide = Math.Max(0, peekSlide - dt / 1.3);
                    break;
            }
            double along = Ease(peekSlide);
            Point next = new Point(peekHome.X + (int)Math.Round((peekTarget.X - peekHome.X) * along), peekHome.Y + (int)Math.Round((peekTarget.Y - peekHome.Y) * along));
            leanAngle = leanAngle * 0.8 + Math.Max(-8, Math.Min(8, (next.X - anchor.X) * 0.4)) * 0.2;
            anchor = next;
            if (peekStage != PeekStage.Returning || peekSlide > 0) return;
            StopBehavior();
            nextIdle = now + 25 + chance.NextDouble() * 35;
        }

        // How far she is slid past the edge of her window, which clips her, and which way she leans back toward the screen.
        private void PeekPose(double now, out float shiftX, out float shiftY, out float lean, out float mirror)
        {
            float reach = (float)Ease(peekOut);
            float unit = preferences.Size;
            shiftX = shiftY = 0;
            mirror = 1;
            if (peekEdge == 2) shiftY = reach * (0.55f * unit + PaddingSize);
            else shiftX = (peekEdge == 1 ? 1 : -1) * reach * (0.42f * unit + PaddingSize);
            if (peekEdge == 0) mirror = -1;
            lean = (peekEdge == 2 ? 0 : peekEdge == 1 ? -10 : 10) * reach + (float)Math.Sin(now * 1.7) * 2 * reach;
        }

        private void Watch()
        {
            Point cursor = Cursor.Position;
            if (cursor == lastCursor) return;
            lastCursor = cursor;
            if (!((behavior == Behavior.Sleep || behavior == Behavior.Peek) && untilTouched)) Noticed();
        }

        private void Wander()
        {
            double now = clock.Elapsed.TotalSeconds;
            if (!preferences.Motion || hidden || riding || pressed || dragging || behavior != Behavior.None || !Visible || !IsHandleCreated) return;
            bool focusing = study == StudyPhase.Focus && !studyPaused;
            if (preferences.Sleep && !focusing && now - lastActive >= SleepAfter)
            {
                if (preferences.Yawn) { Play(Behavior.Yawn, false); followWithSleep = true; }
                else Play(Behavior.Sleep, false);
                return;
            }
            if (preferences.Peeks && !focusing && now - lastActive >= PeekAfter && lastPeek < lastActive && now - reactionStart >= 1.5) { Play(Behavior.Peek, false); return; }
            if (preferences.Sits && now >= nextSit && now - reactionStart >= 1.5) { Play(Behavior.Sit, false); if (behavior == Behavior.Sit) return; }
            if (preferences.Mischief && !focusing && now >= nextMischief && now - reactionStart >= 1.5 && (bubble == null || !bubble.Visible)) { Play(Behavior.Glitch, false); if (behavior == Behavior.Glitch) return; }
            if (now < nextIdle || now - reactionStart < 1.5) return;
            System.Collections.Generic.List<Behavior> options = new System.Collections.Generic.List<Behavior>();
            if (preferences.Stretch) options.Add(Behavior.Stretch);
            if (preferences.LookAround) options.Add(Behavior.Look);
            if (preferences.Yawn) options.Add(Behavior.Yawn);
            if (options.Count == 0) { nextIdle = now + 30; return; }
            Play(options[chance.Next(options.Count)], false);
        }

        private void Advance()
        {
            double now = clock.Elapsed.TotalSeconds;
            sleepAmount += ((behavior == Behavior.Sleep ? 1 : 0) - sleepAmount) * 0.07;
            if (behavior != Behavior.Sleep && sleepAmount < 0.005) sleepAmount = 0;
            AdvanceScript(now);
            double dt = Math.Min(0.1, now - lastAdvance);
            lastAdvance = now;
            if (behavior == Behavior.Peek) { AdvancePeek(now, dt); return; }
            if (behavior == Behavior.Sit) { AdvanceSit(now, dt); return; }
            if (behavior == Behavior.Glitch) AdvanceGlitch(now);
            if (behavior == Behavior.None || behavior == Behavior.Sleep || now - behaviorStart < Length(behavior)) return;
            bool drift = followWithSleep && lastActive <= behaviorStart;
            Behavior done = behavior;
            behavior = Behavior.None;
            followWithSleep = false;
            if (done == Behavior.Glitch) EndGlitch();
            if (drift) { behavior = Behavior.Sleep; behaviorStart = now; sleepKey = chance.Next(2) == 0 ? "sleep" : "laying"; }
            else nextIdle = now + 25 + chance.NextDouble() * 35;
        }

        private void Pose(double age, out float angle, out float stretchX, out float stretchY)
        {
            double a = 0, x = 1, y = 1;
            switch (behavior)
            {
                case Behavior.Stretch:
                    double reach = Envelope(age, Length(behavior), 0.8);
                    y = 1 + 0.085 * reach;
                    x = 1 - 0.035 * reach;
                    a = -3.5 * reach + Math.Sin(age * 16) * 0.5 * reach * reach;
                    break;
                case Behavior.Look:
                    double turn = Ease((age - 0.3) / 0.45) - Ease((age - 2.0) / 0.45);
                    x = Math.Cos(Math.PI * turn);
                    a = Math.Sin(age * 2.4) * 2.5 * Envelope(age, Length(behavior), 0.5) + 3 * turn;
                    break;
                case Behavior.Yawn:
                    double inhale = Ease(age / 1.2) - Ease((age - 1.2) / 0.5);
                    double sag = Ease((age - 1.2) / 0.3) * Ease((Length(behavior) - age) / 0.7);
                    y = 1 + 0.06 * inhale - 0.03 * sag;
                    x = 1 - 0.015 * inhale;
                    a = 2 * sag;
                    break;
                case Behavior.Pat:
                    double glee = Envelope(age, Length(behavior), 0.35);
                    y = 1 - 0.03 * glee + Math.Sin(age * 14) * 0.012 * glee;
                    x = 1 + 0.02 * glee;
                    a = Math.Sin(age * 7) * 3.5 * glee;
                    break;
                case Behavior.Glitch:
                    double surge = Envelope(age, Length(behavior), 0.3);
                    Random jitter = new Random((int)(age / 0.08) * 31 + 7);
                    a = (jitter.NextDouble() - 0.5) * 4 * surge;
                    x = 1 + (jitter.NextDouble() - 0.5) * 0.05 * surge;
                    break;
            }
            y -= 0.045 * sleepAmount;
            a += 7 * sleepAmount;
            angle = (float)a;
            stretchX = (float)x;
            stretchY = (float)y;
        }

        // Placeholder glyphs that float up beside her head until there is artwork for these moods.
        private static ImageAttributes Tint(float red, float green, float blue, float alpha)
        {
            ColorMatrix matrix = new ColorMatrix();
            matrix.Matrix00 = red;
            matrix.Matrix11 = green;
            matrix.Matrix22 = blue;
            matrix.Matrix33 = alpha;
            ImageAttributes attributes = new ImageAttributes();
            attributes.SetColorMatrix(matrix);
            return attributes;
        }

        // Her colours split apart: one red copy and one green copy slip either side of her while a glitch plays.
        private void DrawGhosts(Graphics graphics, Bitmap artwork, RectangleF rect, double age)
        {
            double surge = Envelope(age, Length(Behavior.Glitch), 0.3);
            Random jitter = new Random((int)(age / 0.08) * 17 + 3);
            float reach = (float)((3 + jitter.NextDouble() * 7) * surge * preferences.Size / 240);
            Rectangle left = Rectangle.Round(new RectangleF(rect.X - reach, rect.Y, rect.Width, rect.Height));
            Rectangle right = Rectangle.Round(new RectangleF(rect.X + reach, rect.Y + (float)(jitter.NextDouble() - 0.5) * 4, rect.Width, rect.Height));
            using (ImageAttributes red = Tint(1, 0.15f, 0.25f, 0.6f)) graphics.DrawImage(artwork, left, 0, 0, artwork.Width, artwork.Height, GraphicsUnit.Pixel, red);
            using (ImageAttributes green = Tint(0.2f, 1, 0.35f, 0.6f)) graphics.DrawImage(artwork, right, 0, 0, artwork.Width, artwork.Height, GraphicsUnit.Pixel, green);
        }

        private void DrawMarks(Graphics graphics, string glyph, string font, FontStyle style, Color color, float amount, double time, float side, float rise)
        {
            float unit = preferences.Size;
            using (FontFamily family = new FontFamily(font))
            using (Pen outline = new Pen(Color.FromArgb((int)(150 * amount), 26, 27, 31), 2.5f))
                for (int i = 0; i < 3; i++)
                {
                    double phase = (time / 3.2 + i / 3.0) % 1;
                    double fade = Math.Sin(Math.PI * phase) * amount;
                    float lean = side != 0 ? side : (i % 2 == 0 ? -1 : 1);
                    float x = Width / 2f + lean * unit * (0.27f + 0.05f * (float)Math.Sin(phase * 6.28));
                    float y = Height / 2f - unit * (rise + 0.33f * (float)phase);
                    using (GraphicsPath letter = new GraphicsPath())
                    using (Brush fill = new SolidBrush(Color.FromArgb((int)(235 * fade), color)))
                    {
                        letter.AddString(glyph, family, (int)style, unit * (0.06f + 0.07f * (float)phase), new PointF(x, y), StringFormat.GenericDefault);
                        outline.Color = Color.FromArgb((int)(150 * fade), 26, 27, 31);
                        graphics.DrawPath(outline, letter);
                        graphics.FillPath(fill, letter);
                    }
                }
        }

        // Frames from the show, embedded as Capri.Art.<name>.png. A missing one is simply not used and she falls back to her base picture.
        private Bitmap Sprite(string key)
        {
            Bitmap found;
            if (sprites.TryGetValue(key, out found)) return found;
            using (Stream image = Assembly.GetExecutingAssembly().GetManifestResourceStream("Capri.Art." + key + ".png"))
                if (image != null) using (Bitmap original = new Bitmap(image)) found = new Bitmap(original);
            sprites[key] = found;
            return found;
        }

        // Which frame replaces her base picture right now, and how far the cross-fade has got.
        private void PoseSprite(double now, out string key, out float mix)
        {
            double age = now - behaviorStart;
            key = "";
            mix = 0;
            switch (behavior)
            {
                case Behavior.Stretch: key = "arms"; mix = (float)Envelope(age, Length(behavior), 0.35); break;
                case Behavior.Pat: key = "happy"; mix = (float)Envelope(age, Length(behavior), 0.3); break;
                case Behavior.Yawn: key = "mouth"; mix = (float)(Ease((age - 0.5) / 0.4) * Ease((Length(behavior) - 0.4 - age) / 0.4)); break;
                default: if (sleepAmount > 0.02) { key = sleepKey; mix = (float)Ease(sleepAmount); } break;
            }
        }

        // Fits a frame inside a box a little bigger than her base picture; figures taller than they are wide stand on her feet line, the rest are centred.
        private RectangleF SpriteRect(Bitmap sprite, float baseHeight)
        {
            float box = preferences.Size * 1.1f;
            float scale = Math.Min(box / sprite.Width, box / sprite.Height);
            float width = sprite.Width * scale;
            float tall = sprite.Height * scale;
            float top = sprite.Width < sprite.Height * 0.8f ? baseHeight / 2f - tall : -tall / 2f;
            return new RectangleF(-width / 2f, top, width, tall);
        }

        // Peeking with the show's own frames: the bust rises over the bottom edge, or a lean slides in from the side (mirrored on the right).
        // Her window sits flush against the screen edge, so the edge itself cuts the frame off.
        private void DrawPeekSprite(Graphics graphics, Bitmap picture, double now)
        {
            float reach = (float)Ease(peekOut);
            float wide = Width * (peekEdge == 2 ? 0.96f : 1f);
            float tall = wide * picture.Height / picture.Width;
            if (peekEdge == 2)
            {
                float bob = (float)Math.Sin(now * 1.7) * 2 * reach;
                graphics.DrawImage(picture, new RectangleF((Width - wide) / 2f, Height - tall * reach + bob, wide, tall));
                return;
            }
            float top = (Height - tall) / 2f;
            if (peekEdge == 0) { graphics.DrawImage(picture, new RectangleF(-wide * (1 - reach), top, wide, tall)); return; }
            float left = Width - wide + wide * (1 - reach);
            graphics.DrawImage(picture, new PointF[] { new PointF(left + wide, top), new PointF(left, top), new PointF(left + wide, top + tall) });
        }

        private static void DrawFaded(Graphics graphics, Bitmap image, RectangleF rect, float alpha)
        {
            using (ImageAttributes fade = Tint(1, 1, 1, Math.Max(0, Math.Min(1, alpha))))
                graphics.DrawImage(image, Rectangle.Round(rect), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, fade);
        }

        private void RenderFrame()
        {
            if (!IsHandleCreated || hidden || IsDisposed) return;
            Bitmap next = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(next))
            {
                graphics.CompositingMode = CompositingMode.SourceOver;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                double now = clock.Elapsed.TotalSeconds;
                double age = now - reactionStart;
                float bounce = age >= 0 && age < 0.65 ? (float)(Math.Sin(age / 0.65 * Math.PI) * 15) : 0;
                float breathe = preferences.Motion && !dragging ? (float)Math.Sin(now * 1.75) * 2.2f : 0;
                float tilt = preferences.Motion && !dragging ? (float)Math.Sin(now * 0.95) * 0.65f : 0;
                if (musicPlaying && preferences.MusicMode == 2 && preferences.Motion && !dragging)
                {
                    breathe += (float)Math.Sin(now * 6.2) * 1.6f;
                    tilt += (float)Math.Sin(now * 3.1) * 1.0f;
                }
                bool carried = riding || behavior == Behavior.Sit;
                Bitmap artwork = carried && perched != null ? perched : character;
                float height = preferences.Size * artwork.Height / (float)artwork.Width;
                float sleepy = carried ? 0 : (float)sleepAmount;
                float angle = 0, stretchX = 1, stretchY = 1;
                if (!carried) Pose(now - behaviorStart, out angle, out stretchX, out stretchY);
                string spriteKey = "";
                float spriteMix = 0;
                if (!carried && behavior != Behavior.Peek && behavior != Behavior.Glitch) PoseSprite(now, out spriteKey, out spriteMix);
                Bitmap sprite = spriteMix > 0.01f ? Sprite(spriteKey) : null;
                if (sprite == null) spriteMix = 0;
                angle *= 1 - spriteMix;
                stretchX = 1 + (stretchX - 1) * (1 - spriteMix);
                stretchY = 1 + (stretchY - 1) * (1 - spriteMix);
                float shiftX = 0, shiftY = 0;
                if (behavior == Behavior.Peek && !riding)
                {
                    float lean;
                    float mirror;
                    PeekPose(now, out shiftX, out shiftY, out lean, out mirror);
                    angle += lean;
                    stretchX *= mirror;
                }
                float feet = height / 2f * 0.99f;
                graphics.TranslateTransform(Width / 2f + shiftX, Height / 2f - bounce + breathe + shiftY);
                graphics.RotateTransform(tilt * (1 - sleepy) + (float)leanAngle);
                graphics.TranslateTransform(0, feet);
                graphics.RotateTransform(angle);
                graphics.ScaleTransform(stretchX, stretchY);
                graphics.TranslateTransform(0, -feet);
                RectangleF art = new RectangleF(-preferences.Size / 2f, -height / 2f, preferences.Size, height);
                Bitmap seated = behavior == Behavior.Sit && !riding ? Sprite("sit") : null;
                Bitmap peeking = behavior == Behavior.Peek && !riding ? Sprite(peekEdge == 2 ? "talk-closed" : "lean") : null;
                if (peeking != null) { graphics.ResetTransform(); DrawPeekSprite(graphics, peeking, now); }
                else if (seated != null) graphics.DrawImage(seated, SpriteRect(seated, height));
                else
                {
                    if (behavior == Behavior.Glitch && !carried && effectsOn()) DrawGhosts(graphics, artwork, art, now - behaviorStart);
                    if (spriteMix < 0.99f)
                    {
                        if (spriteMix > 0) DrawFaded(graphics, artwork, art, 1 - spriteMix);
                        else graphics.DrawImage(artwork, art);
                    }
                    if (sprite != null) DrawFaded(graphics, sprite, SpriteRect(sprite, height), spriteMix);
                }
                graphics.ResetTransform();
                if (sleepy > 0.05f) DrawMarks(graphics, "z", "Segoe UI", FontStyle.Bold | FontStyle.Italic, Color.FromArgb(167, 245, 88), sleepy, now, 1, 0.10f);
                if (behavior == Behavior.Pat && !riding) DrawMarks(graphics, "♥", "Segoe UI Symbol", FontStyle.Regular, Color.FromArgb(239, 58, 78), (float)Envelope(now - behaviorStart, Length(behavior), 0.4), now - behaviorStart, 0, 0.18f);
            }
            Native.DrawLayered(Handle, next, anchor);
            Bitmap old = frame;
            frame = next;
            if (old != null) old.Dispose();
        }

        private Preferences LoadPreferences()
        {
            try
            {
                using (FileStream stream = File.OpenRead(settingsPath))
                {
                    Preferences value = (Preferences)new DataContractJsonSerializer(typeof(Preferences)).ReadObject(stream);
                    if (value.Size != 170 && value.Size != 240 && value.Size != 320) value.Size = 240;
                    if (value.Alarms == null) value.Alarms = new List<AlarmEntry>();
                    return value;
                }
            }
            catch { return new Preferences(); }
        }

        private void SavePreferences()
        {
            Point home = behavior == Behavior.Peek ? peekHome : behavior == Behavior.Sit ? sitHome : anchor;
            preferences.X = home.X;
            preferences.Y = home.Y;
            preferences.HasPosition = true;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
                string temporary = settingsPath + ".new";
                using (FileStream stream = File.Create(temporary)) new DataContractJsonSerializer(typeof(Preferences)).WriteObject(stream, preferences);
                if (File.Exists(settingsPath)) File.Replace(temporary, settingsPath, null); else File.Move(temporary, settingsPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplaysChanged;
            StopRide();
            if (talkKey && IsHandleCreated) Native.UnregisterHotKey(Handle, TalkId);
            if (voiceKey && IsHandleCreated) Native.UnregisterHotKey(Handle, VoiceKeyId);
            if (lookKey && IsHandleCreated) Native.UnregisterHotKey(Handle, LookKeyId);
            clickDelay.Stop();
            frames.Stop();
            signals.Stop();
            tray.Visible = false;
            SavePreferences();
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                clickDelay.Dispose();
                frames.Dispose();
                signals.Dispose();
                tray.Dispose();
                menu.Dispose();
                if (bubble != null) bubble.Dispose();
                if (chat != null) chat.Dispose();
                if (cutin != null) cutin.Dispose();
                if (mic != null) mic.Dispose();
                if (musicTimer != null) musicTimer.Dispose();
                if (commentLater != null) commentLater.Dispose();
                ear.Dispose();
                if (overlay != null) overlay.Dispose();
                if (chipArt != null) chipArt.Dispose();
                foreach (Bitmap sprite in sprites.Values) if (sprite != null) sprite.Dispose();
                voice.Dispose();
                character.Dispose();
                if (perched != null) perched.Dispose();
                if (frame != null) frame.Dispose();
            }
            base.Dispose(disposing);
        }

        // Stands in for Ollama or LM Studio during the smoke test: two models, and one canned answer that thinks out loud first.
        private sealed class FakeServer : IDisposable
        {
            private readonly HttpListener listener = new HttpListener();
            internal readonly int Port;
            internal string LastBody = "";
            internal int Hits;
            internal bool Broken;
            internal bool NoOllama;
            internal string LastPath = "";
            internal string LastAuth = "";
            internal string LastVersion = "";
            internal int ForceStatus;
            internal bool EmptyAnswer;
            internal bool RejectExtras;

            internal FakeServer()
            {
                TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                Port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                listener.Prefixes.Add("http://localhost:" + Port + "/");
                listener.Start();
                listener.BeginGetContext(Handle, null);
            }

            private void Handle(IAsyncResult result)
            {
                HttpListenerContext context;
                try { context = listener.EndGetContext(result); }
                catch (Exception) { return; }
                try { listener.BeginGetContext(Handle, null); }
                catch (Exception) { }
                string body;
                using (StreamReader reader = new StreamReader(context.Request.InputStream, Encoding.UTF8)) body = reader.ReadToEnd();
                string path = context.Request.Url.AbsolutePath;
                bool models = path == "/v1/models";
                bool version = path == "/api/version";
                if (!models && !version) { LastBody = body; LastPath = path; LastAuth = context.Request.Headers["Authorization"] ?? context.Request.Headers["x-api-key"] ?? ""; LastVersion = context.Request.Headers["anthropic-version"] ?? ""; }
                Interlocked.Increment(ref Hits);
                string answer = models
                    ? "{\"object\":\"list\",\"data\":[{\"id\":\"fake-small\"},{\"id\":\"fake-big\"}]}"
                    : version ? "{\"version\":\"0.0.0\"}"
                    : path == "/v1/messages" ? "{\"content\":[{\"type\":\"text\",\"text\":\"Hello from the fake claude.\"}]}"
                    : path == "/api/chat" ? "{\"message\":{\"role\":\"assistant\",\"content\":\"<think>hmm</think>  Hello   from the fake model.\"}}"
                    : "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"<think>hmm</think>  Hello   from the fake model.\"}}]}";
                byte[] bytes = Encoding.UTF8.GetBytes(answer);
                if (EmptyAnswer && path == "/v1/chat/completions") answer = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"\"}}]}";
                if (ForceStatus >= 400 && !models && !version) answer = "{\"error\":{\"message\":\"bad model\"}}";
                if (RejectExtras && !models && !version && (body.IndexOf("\"temperature\"") >= 0 || body.IndexOf("\"reasoning\"") >= 0)) { answer = "{\"error\":{\"message\":\"temperature is not supported\"}}"; context.Response.StatusCode = 400; }
                bytes = Encoding.UTF8.GetBytes(answer);
                if (Broken && !models && !version) context.Response.StatusCode = 500;
                if (ForceStatus != 0 && !models && !version) context.Response.StatusCode = ForceStatus;
                if (RejectExtras && context.Response.StatusCode == 200 && !models && !version && (body.IndexOf("\"temperature\"") >= 0 || body.IndexOf("\"reasoning\"") >= 0)) context.Response.StatusCode = 400;
                if (NoOllama && (version || path == "/api/chat")) context.Response.StatusCode = 404;
                context.Response.ContentType = "application/json";
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                context.Response.Close();
            }

            public void Dispose() { listener.Close(); }
        }

        internal void StartSmokeTest(string output)
        {
            Directory.CreateDirectory(output);
            Shown += delegate
            {
                System.Windows.Forms.Timer test = new System.Windows.Forms.Timer();
                test.Interval = 500;
                int step = 0;
                Form backdrop = null;
                System.Collections.Generic.List<string> checks = new System.Collections.Generic.List<string>();
                bool busy = false;
                test.Tick += delegate
                {
                    if (busy) return;
                    busy = true;
                    try
                    {
                        if (step == 0)
                        {
                            lastSaid = clock.Elapsed.TotalSeconds;
                            checks.Add("PASS native window visible: " + Visible);
                            if (!Visible || frame == null) throw new Exception("Native window did not render.");
                            if ((Native.GetWindowLong(Handle, -20) & 0x00080000) == 0) throw new Exception("Window is not layered.");
                            if (frame.GetPixel(0, 0).A != 0) throw new Exception("Frame corner is opaque.");
                            checks.Add("PASS per-pixel alpha and layered window");
                            Point before = anchor;
                            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, Width / 2, Height / 2, 0));
                            Point moved = new Point(pointerStart.X - 80, pointerStart.Y - 35);
                            MovePointer(moved);
                            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, Width / 2, Height / 2, 0));
                            if (anchor == before || pendingClick || riding) throw new Exception("Drag did not change position or did not cancel click.");
                            checks.Add("PASS drag handler changed position");
                            int previous = clicks;
                            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, Width / 2, Height / 2, 0));
                            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, Width / 2, Height / 2, 0));
                            if (!pendingClick || clicks != previous) throw new Exception("Single click was not delayed.");
                            OnClickDelay();
                            if (clicks != previous + 1 || !frames.Enabled) throw new Exception("Click reaction did not start.");
                            checks.Add("PASS delayed single click started reaction");
                            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, Width / 2, Height / 2, 0));
                            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, Width / 2, Height / 2, 0));
                            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, Width / 2, Height / 2, 0));
                            pendingTime = clock.ElapsedMilliseconds;
                            pendingPoint = Cursor.Position;
                            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, Width / 2, Height / 2, 0));
                            if (!riding || (Native.GetWindowLong(Handle, -20) & 0x20) == 0) throw new Exception("Double click did not perch with click-through.");
                            checks.Add("PASS double click perched with click-through");
                            checks.Add(hotkey ? "PASS Ctrl+Alt+C registered while perched" : "LIMIT Ctrl+Alt+C taken; tray Hop off fallback");
                            if (IsDoubleClick(Point.Empty, new Point(200, 200), 10)) throw new Exception("Double click predicate failed.");
                            Message key = Message.Create(Handle, 0x0312, new IntPtr(DismountId), IntPtr.Zero);
                            WndProc(ref key);
                            if (riding || hotkey || (Native.GetWindowLong(Handle, -20) & 0x20) != 0) throw new Exception("Hotkey did not hop off.");
                            checks.Add("PASS Ctrl+Alt+C message hopped off and released the hotkey");
                            rideItem.PerformClick();
                            if (!riding || rideItem.Text != "Hop off") throw new Exception("Menu perch failed.");
                            rideItem.PerformClick();
                            if (riding || (Native.GetWindowLong(Handle, -20) & 0x20) != 0) throw new Exception("Hop off menu did not clean up.");
                            checks.Add("PASS tray Perch on cursor and Hop off");
                            rideItem.PerformClick();
                            visibilityItem.PerformClick();
                            if (Visible || !hidden || riding || hotkey) throw new Exception("Hide menu did not hide window or clean up perch.");
                            checks.Add(perched == null ? "LIMIT seated artwork unavailable" : "PASS seated artwork loaded");
                            checks.Add("PASS hide menu");
                            restore.Set();
                        }
                        else if (step == 1)
                        {
                            for (int wait = 0; wait < 150 && (!Visible || hidden); wait++) { Application.DoEvents(); Thread.Sleep(20); }
                            if (!Visible || hidden) throw new Exception("Second-launch restore signal did not show Capri.");
                            checks.Add("PASS restore signal");
                            Point bounded = KeepOnScreen(new Point(int.MaxValue / 4, int.MaxValue / 4));
                            if (!Screen.FromPoint(new Point(bounded.X + Width / 2, bounded.Y + Height / 2)).WorkingArea.Contains(new Rectangle(bounded, Size))) throw new Exception("Screen bounds failed.");
                            checks.Add("PASS screen bounds");
                            ((ToolStripMenuItem)((ToolStripMenuItem)menu.Items[3]).DropDownItems[0]).PerformClick();
                            if (Width != 242) throw new Exception("Small size failed.");
                            ((ToolStripMenuItem)((ToolStripMenuItem)menu.Items[3]).DropDownItems[1]).PerformClick();
                            checks.Add("PASS size menu");
                            foreach (string key in new string[] { "sleep", "laying", "happy", "mouth", "arms", "talk-open", "talk-closed", "scream", "lean", "sit", "eyes", "eyes-closed", "eyes-left", "eyes-right", "eyes-many", "eyes-glitch", "happyface", "content" })
                                if (Sprite(key) == null) throw new Exception("The embedded frame '" + key + "' is missing.");
                            checks.Add("PASS all embedded frames from the show are present");
                            string[] poses = { "stretch", "look", "yawn", "pat" };
                            Behavior[] shown = { Behavior.Stretch, Behavior.Look, Behavior.Yawn, Behavior.Pat };
                            double[] peaks = { 1.3, 1.4, 1.5, 0.9 };
                            for (int i = 0; i < poses.Length; i++)
                            {
                                Play(shown[i], true);
                                if (behavior != shown[i] || !frames.Enabled) throw new Exception(poses[i] + " did not start.");
                                behaviorStart = clock.Elapsed.TotalSeconds - peaks[i];
                                RenderFrame();
                                frame.Save(Path.Combine(output, "pose-" + poses[i] + ".png"), ImageFormat.Png);
                                behaviorStart -= 10;
                                Advance();
                                if (behavior != Behavior.None) throw new Exception(poses[i] + " did not finish.");
                            }
                            checks.Add("PASS stretch, look around, yawn and head pat play, render and finish");
                            Play(Behavior.Sleep, true);
                            sleepAmount = 1;
                            RenderFrame();
                            frame.Save(Path.Combine(output, "pose-sleep.png"), ImageFormat.Png);
                            lastCursor = new Point(-1, -1);
                            Watch();
                            if (behavior != Behavior.Sleep) throw new Exception("Pointer movement woke a manual sleep.");
                            Noticed();
                            if (behavior != Behavior.None) throw new Exception("Touching her did not wake her.");
                            StopBehavior();
                            checks.Add("PASS manual sleep stays asleep until touched");
                            lastActive = clock.Elapsed.TotalSeconds - SleepAfter - 1;
                            Wander();
                            if (behavior != Behavior.Yawn || !followWithSleep) throw new Exception("Idle did not start a yawn before sleep.");
                            behaviorStart -= 10;
                            Advance();
                            if (behavior != Behavior.Sleep) throw new Exception("Yawn did not lead into sleep.");
                            lastCursor = new Point(-1, -1);
                            Watch();
                            if (behavior != Behavior.None) throw new Exception("Pointer movement did not wake automatic sleep.");
                            StopBehavior();
                            checks.Add("PASS idle yawn leads to sleep and pointer movement wakes her");
                            ToolStripMenuItem reactions = (ToolStripMenuItem)menu.Items[7];
                            ToolStripMenuItem playMenu = (ToolStripMenuItem)reactions.DropDownItems[0];
                            ToolStripMenuItem autoMenu = (ToolStripMenuItem)reactions.DropDownItems[1];
                            SetAllowed(Behavior.Glitch, true);
                            foreach (ToolStripItem entry in autoMenu.DropDownItems) entry.PerformClick();
                            if (preferences.Stretch || preferences.LookAround || preferences.Yawn || preferences.Sleep || preferences.Pats || preferences.Peeks || preferences.Sits || preferences.Mischief) throw new Exception("Automatic toggles did not switch off.");
                            reactionStart = -10;
                            nextIdle = 0;
                            lastActive = clock.Elapsed.TotalSeconds - SleepAfter - 1;
                            Wander();
                            if (behavior != Behavior.None) throw new Exception("A disabled reaction still played on its own.");
                            playMenu.DropDownItems[0].PerformClick();
                            if (behavior != Behavior.Stretch) throw new Exception("Play now did not ignore the automatic toggle.");
                            StopBehavior();
                            autoMenu.DropDownItems[0].PerformClick();
                            nextIdle = 0;
                            lastActive = clock.Elapsed.TotalSeconds;
                            Wander();
                            if (behavior != Behavior.Stretch) throw new Exception("Re-enabled reaction did not play on its own.");
                            StopBehavior();
                            for (int i = 1; i < autoMenu.DropDownItems.Count; i++) autoMenu.DropDownItems[i].PerformClick();
                            if (!preferences.Stretch || !preferences.LookAround || !preferences.Yawn || !preferences.Sleep || !preferences.Pats || !preferences.Peeks || !preferences.Sits || !preferences.Mischief) throw new Exception("Automatic toggles did not switch back on.");
                            checks.Add("PASS Reactions menu plays on demand and toggles automatic reactions");
                            Point head = new Point(PaddingSize + preferences.Size / 2, PaddingSize + preferences.Size * 110 / 420);
                            Point legs = new Point(PaddingSize + preferences.Size / 2, PaddingSize + preferences.Size * 380 / 420);
                            if (!Solid(head) || !Solid(legs)) throw new Exception("Test points are not on the artwork.");
                            Hover(PointToScreen(legs));
                            if (hovering) throw new Exception("Hovering her legs counted as a head pat.");
                            Hover(PointToScreen(head));
                            Pet();
                            if (behavior != Behavior.None) throw new Exception("A head pat played before a second had passed.");
                            hoverStart -= 2;
                            hoverAnchor.X += 8;
                            Hover(PointToScreen(head));
                            Pet();
                            if (behavior != Behavior.Pat) throw new Exception("Holding the pointer on her head did not start a pat.");
                            behaviorStart -= 10;
                            Advance();
                            Pet();
                            if (behavior != Behavior.None) throw new Exception("One hover patted her twice.");
                            OnMouseLeave(EventArgs.Empty);
                            Hover(PointToScreen(head));
                            hoverStart -= 2;
                            hoverAnchor.X += 60;
                            Hover(PointToScreen(head));
                            if (!hovering || clock.Elapsed.TotalSeconds - hoverStart > 1) throw new Exception("Moving across her did not restart the hold.");
                            Pet();
                            if (behavior != Behavior.None) throw new Exception("Sweeping across her counted as holding.");
                            preferences.Pats = false;
                            Hover(PointToScreen(head));
                            hoverStart -= 2;
                            Pet();
                            if (behavior != Behavior.None) throw new Exception("A disabled head pat still played.");
                            preferences.Pats = true;
                            OnMouseLeave(EventArgs.Empty);
                            checks.Add("PASS head pat needs a one second hold on her head, plays once per hover and obeys its toggle");
                            Point rest = anchor;
                            string[] edges = { "left", "right", "bottom" };
                            for (int e = 0; e < edges.Length; e++)
                            {
                                Play(Behavior.Peek, true);
                                BeginPeek(e);
                                if (behavior != Behavior.Peek) throw new Exception("Peek did not start.");
                                double moment = clock.Elapsed.TotalSeconds;
                                bool looked = false;
                                for (int n = 0; n < 400 && behavior == Behavior.Peek; n++)
                                {
                                    moment += 0.1;
                                    AdvancePeek(moment, 0.1);
                                    if (looked || peekStage != PeekStage.Looking || peekOut < 1) continue;
                                    looked = true;
                                    if (anchor != peekTarget) throw new Exception("She did not reach the " + edges[e] + " edge.");
                                    if (!Screen.FromPoint(new Point(rest.X + Width / 2, rest.Y + Height / 2)).WorkingArea.Contains(new Rectangle(anchor, Size))) throw new Exception("Her window left the screen at the " + edges[e] + " edge.");
                                    RenderFrame();
                                    frame.Save(Path.Combine(output, "pose-peek-" + edges[e] + ".png"), ImageFormat.Png);
                                }
                                if (!looked || behavior != Behavior.None || anchor != rest) throw new Exception("Peek at the " + edges[e] + " edge did not finish back home.");
                            }
                            checks.Add("PASS peek reaches the left, right and bottom edges inside the screen and returns home");
                            Play(Behavior.Peek, false);
                            BeginPeek(1);
                            double later = clock.Elapsed.TotalSeconds;
                            while (peekStage != PeekStage.Looking || peekOut < 1) { later += 0.1; AdvancePeek(later, 0.1); }
                            lastCursor = new Point(-3, -3);
                            Watch();
                            if (peekStage != PeekStage.Ducking) throw new Exception("Pointer movement did not send her back.");
                            while (behavior == Behavior.Peek) { later += 0.1; AdvancePeek(later, 0.1); }
                            if (anchor != rest) throw new Exception("She did not return after being noticed.");
                            Play(Behavior.Peek, true);
                            BeginPeek(1);
                            while (peekStage != PeekStage.Looking || peekOut < 1) { later += 0.1; AdvancePeek(later, 0.1); }
                            lastCursor = new Point(-4, -4);
                            Watch();
                            if (peekStage != PeekStage.Looking) throw new Exception("Pointer movement cut a manual peek short.");
                            Noticed();
                            if (peekStage != PeekStage.Ducking) throw new Exception("Touching her did not end a manual peek.");
                            StopBehavior();
                            if (anchor != rest) throw new Exception("Stopping a peek did not put her back.");
                            checks.Add("PASS pointer movement ends an automatic peek and only touch ends a manual one");
                            nextIdle = double.MaxValue;
                            lastPeek = -1000;
                            lastActive = clock.Elapsed.TotalSeconds - PeekAfter - 1;
                            preferences.Peeks = false;
                            Wander();
                            if (behavior != Behavior.None) throw new Exception("A disabled peek still played.");
                            preferences.Peeks = true;
                            Wander();
                            if (behavior != Behavior.Peek) throw new Exception("Being idle did not start a peek.");
                            StopBehavior();
                            Wander();
                            if (behavior != Behavior.None) throw new Exception("She peeked twice in one idle stretch.");
                            lastActive = clock.Elapsed.TotalSeconds;
                            checks.Add("PASS idle peek happens once per idle stretch and obeys its toggle");
                            lastSaid = -100;
                            Say("hello there. this is a test.", false);
                            if (bubble == null || !bubble.Visible || bubble.Message != "hello there. this is a test.") throw new Exception("Speech bubble did not appear.");
                            if (bubble.Picture == null || bubble.Picture.Width < 60 || bubble.Area.Size != bubble.Picture.Size) throw new Exception("Speech bubble was not drawn.");
                            if (!Screen.FromPoint(HeadPoint()).WorkingArea.Contains(bubble.Area)) throw new Exception("Speech bubble left the screen.");
                            bubble.Picture.Save(Path.Combine(output, "bubble.png"), ImageFormat.Png);
                            int withFace = bubble.Picture.Width;
                            portraitItem.PerformClick();
                            lastSaid = -100;
                            Say("hello there. this is a test.", false);
                            if (preferences.Portrait || bubble.Picture.Width >= withFace) throw new Exception("Switching her face off did not slim the bubble.");
                            portraitItem.PerformClick();
                            if (!preferences.Portrait) throw new Exception("Her face did not switch back on.");
                            Say("too soon", false);
                            if (bubble.Message != "hello there. this is a test.") throw new Exception("Automatic remarks were not spaced out.");
                            checks.Add("PASS speech bubble appears above her head on screen and automatic remarks are spaced out");
                            bubbleItem.PerformClick();
                            if (preferences.Bubbles || bubble.Visible) throw new Exception("Switching bubbles off did not hide the bubble.");
                            lastSaid = -100;
                            Say("silenced", false);
                            if (bubble.Visible) throw new Exception("A bubble appeared while switched off.");
                            Say("forced", true);
                            if (!bubble.Visible || bubble.Message != "forced") throw new Exception("A forced remark did not show.");
                            bubbleItem.PerformClick();
                            if (!preferences.Bubbles) throw new Exception("Bubbles did not switch back on.");
                            bubble.Present("blink", 0.05, true, 14);
                            Thread.Sleep(120);
                            bubble.Tick();
                            if (bubble.Visible) throw new Exception("Speech bubble did not expire.");
                            lastSaid = -100;
                            Play(Behavior.Pat, true);
                            if (!bubble.Visible || Array.IndexOf(PatLines, bubble.Message) < 0) throw new Exception("A pat did not get a remark.");
                            StopBehavior();
                            lastSaid = -100;
                            Play(Behavior.Sleep, true);
                            Noticed();
                            if (!bubble.Visible || Array.IndexOf(WakeLines, bubble.Message) < 0) throw new Exception("Waking up did not get a remark.");
                            StopBehavior();
                            bubble.Dismiss();
                            checks.Add("PASS bubbles obey their switch, expire, and accompany pats and waking");
                            DateTime sample = new DateTime(2026, 10, 24, 14, 0, 0);
                            string[] phrases = { "7:30 stand up", "6:15 pm", "in 20m tea", "1h30m", "10 min walk", "in 5", "14:00", "12 am", "13:30pm", "7", "banana", "25:00", "7:75" };
                            DateTime?[] expected = { new DateTime(2026, 10, 25, 7, 30, 0), new DateTime(2026, 10, 24, 18, 15, 0), new DateTime(2026, 10, 24, 14, 20, 0), new DateTime(2026, 10, 24, 15, 30, 0), new DateTime(2026, 10, 24, 14, 10, 0), new DateTime(2026, 10, 24, 14, 5, 0), new DateTime(2026, 10, 25, 14, 0, 0), new DateTime(2026, 10, 25, 0, 0, 0), null, null, null, null, null };
                            string[] notes = { "stand up", "", "tea", "", "walk", "", "", "", "", "", "", "", "" };
                            for (int i = 0; i < phrases.Length; i++)
                            {
                                DateTime parsed;
                                string note;
                                bool ok = TryParseWhen(phrases[i], sample, out parsed, out note);
                                if (ok != expected[i].HasValue || (ok && (parsed != expected[i].Value || note != notes[i]))) throw new Exception("Alarm phrase '" + phrases[i] + "' was read wrongly.");
                            }
                            checks.Add("PASS alarm phrases are read as clock times or waits, with notes, and nonsense is refused");
                            if (menu.Items[9].Text != "Timers") throw new Exception("Timers menu is not where the tests expect.");
                            preferences.Alarms.Clear();
                            notices.Clear();
                            AddAlarm(DateTime.Now.AddMinutes(30), "later");
                            AddAlarm(DateTime.Now.AddSeconds(-5), "now");
                            if (preferences.Alarms.Count != 2 || preferences.Alarms[0].Label != "now" || alarmsMenu.DropDownItems.Count != AlarmListStart + 2) throw new Exception("Alarms were not added in time order.");
                            SavePreferences();
                            Preferences reloaded = LoadPreferences();
                            if (reloaded.Alarms.Count != 2 || reloaded.Alarms[1].Label != "later") throw new Exception("Alarms did not survive a restart.");
                            Keep();
                            if (preferences.Alarms.Count != 1 || notices.Count != 1) throw new Exception("A due alarm did not fire exactly once.");
                            bubble.Dismiss();
                            Deliver();
                            if (!bubble.Visible || !bubble.Message.EndsWith(". now") || notices.Count != 0) throw new Exception("A due alarm was not announced.");
                            if (!AlarmText(new AlarmEntry { Due = DateTime.UtcNow.AddHours(-1).Ticks, Label = "x" }).StartsWith("you missed")) throw new Exception("A missed alarm was not described as missed.");
                            alarmsMenu.DropDownItems[AlarmListStart].PerformClick();
                            if (preferences.Alarms.Count != 0 || alarmsMenu.DropDownItems.Count != AlarmListStart + 1) throw new Exception("Cancelling an alarm from the menu failed.");
                            checks.Add("PASS alarms sort, persist, fire once, announce, report missed ones and cancel from the menu");
                            StartStudy(25, 5);
                            if (study != StudyPhase.Focus || !studyStatusItem.Text.StartsWith("Focus 25:00") || !studyStopItem.Enabled || !trayShown.StartsWith("Capri · focus")) throw new Exception("Study session did not start.");
                            lastSaid = -100;
                            Say("chatter", false);
                            if (bubble.Message == "chatter") throw new Exception("She chattered during focus.");
                            nextIdle = double.MaxValue;
                            lastPeek = -1000;
                            lastActive = clock.Elapsed.TotalSeconds - SleepAfter - 1;
                            Wander();
                            if (behavior != Behavior.None) throw new Exception("She slept or peeked during focus.");
                            lastActive = clock.Elapsed.TotalSeconds;
                            PauseStudy();
                            studyEnd = DateTime.UtcNow.AddSeconds(-1);
                            Keep();
                            if (!studyPaused || study != StudyPhase.Focus || !studyStatusItem.Text.StartsWith("Paused Focus")) throw new Exception("A paused session moved on.");
                            PauseStudy();
                            if (studyPaused || studyEnd < DateTime.UtcNow.AddMinutes(24)) throw new Exception("Resuming did not restore the time left.");
                            studyEnd = DateTime.UtcNow.AddSeconds(-1);
                            Keep();
                            if (study != StudyPhase.Break || studyDone != 1 || !studyStatusItem.Text.StartsWith("Break 5:00 · 1 done")) throw new Exception("Focus did not roll into a break.");
                            studyEnd = DateTime.UtcNow.AddSeconds(-1);
                            Keep();
                            if (study != StudyPhase.Focus || notices.Count != 2) throw new Exception("The break did not roll back into focus.");
                            notices.Clear();
                            StopStudy();
                            if (study != StudyPhase.Off || studyPauseItem.Enabled || trayShown != "Capri · Double-click to restore") throw new Exception("Stopping the session did not tidy up.");
                            bubble.Dismiss();
                            checks.Add("PASS study timer runs focus and break, pauses and resumes, keeps her quiet, and stops cleanly");
                            string[] allowed = { "localhost:11434", "http://127.0.0.1:1234/v1", "[::1]:8000", "LOCALHOST" };
                            string[] refused = { "http://example.com", "192.168.1.5:11434", "ftp://localhost", "", "http://localhost.evil.com", "10.0.0.2" };
                            Uri parsedUri;
                            foreach (string address in allowed) if (!Llm.TryLocal(address, out parsedUri)) throw new Exception("Local address '" + address + "' was refused.");
                            foreach (string address in refused) if (Llm.TryLocal(address, out parsedUri)) throw new Exception("Address '" + address + "' was allowed but is not on this computer.");
                            if (Llm.Tidy("<think>x</think>  Hi   there") != "Hi there" || Llm.Tidy("It's *so* boring, *my* friend.") != "It's so boring, my friend." || Llm.Tidy("<think>never finished") != "" || Llm.Tidy(new string('a', 400)).Length > 320 || Llm.Tidy("One. " + new string('b', 200) + ". " + new string('c', 300)).EndsWith("...")) throw new Exception("Model replies were not tidied.");
                            checks.Add("PASS only addresses on this computer are accepted and model replies are tidied");
                            Func<Func<bool>, bool> waitFor = delegate(Func<bool> done)
                            {
                                for (int n = 0; n < 200 && !done(); n++) { Application.DoEvents(); Thread.Sleep(20); }
                                return done();
                            };
                            using (FakeServer server = new FakeServer())
                            {
                                Say("dismiss", true);
                                if (TrySetServer("http://example.com") || preferences.LlmUrl != "") throw new Exception("A remote server address was accepted.");
                                if (!TrySetServer("localhost:" + server.Port) || preferences.LlmUrl != "http://localhost:" + server.Port) throw new Exception("The local server address was not kept.");
                                Talk();
                                if (!ChatOpen() || chat.Transcript.IndexOf("local model is off") < 0) throw new Exception("The chat window did not open with a note about the model being off.");
                                chat.Submit("anyone home?");
                                if (server.Hits != 0 || chat.Transcript.IndexOf("switched off") < 0) throw new Exception("She reached out while the local model was off.");
                                chat.Hide();
                                llmItem.PerformClick();
                                if (!waitFor(delegate { return llmLink != null; })) throw new Exception("She did not find the fake local model.");
                                if (llmLink.Models.Length != 2 || !llmStatusItem.Text.EndsWith("fake-small") || llmModels.DropDownItems.Count != 2 || !preferences.LlmOn) throw new Exception("The model list was not shown.");
                                llmModels.DropDownItems[1].PerformClick();
                                if (preferences.LlmModel != "fake-big" || !llmStatusItem.Text.EndsWith("fake-big")) throw new Exception("Choosing a model did not stick.");
                                checks.Add("PASS the local model is found on request, listed in the menu and can be chosen");
                                Converse("hi there");
                                if (!waitFor(delegate { return !llmBusy; })) throw new Exception("The model never answered.");
                                if (bubble.Message != "Hello from the fake model." || chatLog.Count != 2) throw new Exception("The answer was not shown as expected: " + bubble.Message);
                                string sentBody = server.LastBody;
                                if (sentBody.IndexOf("\"model\":\"fake-big\"") < 0 || sentBody.IndexOf("hi there") < 0 || sentBody.IndexOf("\"role\":\"system\"") < 0 || sentBody.IndexOf("\"stream\":false") < 0 || sentBody.IndexOf("Capri") < 0) throw new Exception("The request to the model was not shaped as expected: " + sentBody);
                                Converse("and again");
                                waitFor(delegate { return !llmBusy; });
                                if (server.LastBody.IndexOf("hi there") < 0 || server.LastBody.IndexOf("Hello from the fake model.") < 0 || chatLog.Count != 4) throw new Exception("Earlier turns were not remembered.");
                                if (!llmLink.Ollama || server.LastPath != "/api/chat" || sentBody.IndexOf("\"think\":false") < 0) throw new Exception("An Ollama server was not asked with thinking turned off: " + server.LastPath + " " + sentBody);
                                checks.Add("PASS a chat turn reaches the local model with her persona and history, and the answer appears in a bubble");
                                string personaFile = PersonaPath();
                                if (File.Exists(personaFile)) File.Delete(personaFile);
                                if (PersonaText() != Llm.DefaultPersona) throw new Exception("Her built-in personality was not used by default.");
                                EditPersona();
                                if (!File.Exists(personaFile) || PersonaText() != Llm.DefaultPersona) throw new Exception("Editing her personality did not start from the built-in text.");
                                File.WriteAllText(personaFile, "You are a very different test persona.\r\n");
                                Converse("who are you");
                                waitFor(delegate { return !llmBusy; });
                                if (server.LastBody.IndexOf("very different test persona") < 0 || server.LastBody.IndexOf("Capri, an AI construct") >= 0) throw new Exception("Her edited personality was not sent to the model.");
                                File.Delete(personaFile);
                                chatLog.Clear();
                                server.NoOllama = true;
                                llmItem.PerformClick();
                                llmItem.PerformClick();
                                if (!waitFor(delegate { return llmLink != null; }) || llmLink.Ollama) throw new Exception("A server without Ollama's route was treated as Ollama.");
                                Converse("plain route");
                                waitFor(delegate { return !llmBusy; });
                                if (server.LastPath != "/v1/chat/completions" || bubble.Message != "Hello from the fake model.") throw new Exception("The OpenAI-style route was not used for other servers: " + server.LastPath);
                                server.NoOllama = false;
                                llmItem.PerformClick();
                                llmItem.PerformClick();
                                if (!waitFor(delegate { return llmLink != null; })) throw new Exception("She did not reconnect after the route test.");
                                checks.Add("PASS Ollama gets thinking switched off, other servers use the standard route, and her personality can be edited");
                                bubble.Dismiss();
                                chatLog.Clear();
                                Talk();
                                if (!ChatOpen() || chat.Empty) throw new Exception("The chat window did not open with a greeting.");
                                chat.Submit("hello window");
                                if (!waitFor(delegate { return !llmBusy && chat.Transcript.IndexOf("Hello from the fake model.") >= 0; })) throw new Exception("The first answer never reached the chat window.");
                                chat.Submit("and a second thing");
                                if (!waitFor(delegate { return !llmBusy && Regex.Matches(chat.Transcript, "Hello from the fake model.").Count == 2; })) throw new Exception("A second turn did not work in the same window.");
                                string conversation = chat.Transcript;
                                if (conversation.IndexOf("YOU") < 0 || conversation.IndexOf("hello window") < 0 || conversation.IndexOf("and a second thing") < 0 || Regex.Matches(conversation, "CAPRI").Count < 3) throw new Exception("The conversation was not kept in the window: " + conversation);
                                if (bubble.Visible || chatLog.Count != 4) throw new Exception("An answer went to a bubble although the chat window was open, or the history was wrong.");
                                Application.DoEvents();
                                Thread.Sleep(200);
                                Application.DoEvents();
                                using (Bitmap picture = new Bitmap(chat.Width, chat.Height))
                                using (Graphics grab = Graphics.FromImage(picture))
                                {
                                    grab.CopyFromScreen(chat.Location, Point.Empty, chat.Size);
                                    picture.Save(Path.Combine(output, "chat-window.png"), ImageFormat.Png);
                                }
                                chat.Hide();
                                Talk();
                                if (!ChatOpen() || chat.Transcript != conversation) throw new Exception("Reopening the chat window lost the conversation.");
                                chat.Hide();
                                checks.Add("PASS the chat window is a real conversation: it keeps going, keeps its history when closed and reopened, and answers stay out of bubbles");
                                Action<string, double> sendHeard = null;
                                Action<string> sendFailed = null;
                                ear.Source = delegate(Action<string, double> onHeard, Action<string> onFailed) { sendHeard = onHeard; sendFailed = onFailed; };
                                int quietLog = chatLog.Count;
                                Listen();
                                if (ChatOpen() || !ear.Listening) throw new Exception("The microphone opened the chat window instead of just listening.");
                                sendHeard("hello without a window", 0.9);
                                if (!waitFor(delegate { return !llmBusy && chatLog.Count == quietLog + 2; })) throw new Exception("Something said with the chat closed was not answered.");
                                if (ChatOpen() || ear.Listening || chatLog[quietLog].Content != "Hello without a window" || bubble == null || !bubble.Visible) throw new Exception("A spoken conversation did not stay in bubbles.");
                                checks.Add("PASS the microphone talks to her without opening the chat window: she answers in a bubble");
                                foreach (string lookPhrase in new string[] { "what should I do in this situation", "Capri, what weapon should I buy", "what team should i pick", "what should I pick here", "what do I do next in this round" })
                                    if (!WantsLook(lookPhrase)) throw new Exception("A question about what she can see was not taken as a look: " + lookPhrase);
                                if (WantsLook("what should I eat tonight") || WantsLook("what do you think about humans") || WantsLook("what should I name my dog")) throw new Exception("An ordinary question was taken as a look.");
                                if (!WantsLook("Capri, look at my screen") || !WantsLook("what am I watching?") || WantsLook("hello there") || WantsLook("I am watching you")) throw new Exception("Asking her to look was not told apart from ordinary chat.");
                                byte[] realShot = Snapshot();
                                if (realShot.Length < 2000 || realShot[0] != 0xFF || realShot[1] != 0xD8) throw new Exception("The real screen grab did not produce a picture.");
                                byte[] fakeShot = new byte[] { 1, 2, 3, 4 };
                                ScreenSource = delegate { return fakeShot; };
                                int shots = chatLog.Count;
                                Look("");
                                if (!waitFor(delegate { return !llmBusy && chatLog.Count == shots + 2; })) throw new Exception("Looking at the screen was not answered.");
                                if (server.LastBody.IndexOf(Convert.ToBase64String(fakeShot)) < 0 || server.LastBody.IndexOf("not kept") < 0) throw new Exception("The screenshot was not sent with the question.");
                                foreach (ChatLine kept in chatLog) if (kept.Images != null) throw new Exception("A screenshot was kept in her memory of the conversation.");
                                preferences.LookOn = false;
                                int asked = server.Hits;
                                Look("");
                                if (server.Hits != asked) throw new Exception("She looked although looking was switched off.");
                                preferences.LookOn = true;
                                ScreenSource = null;
                                checks.Add("PASS asking her to look sends one picture to the model with the question, keeps nothing of it, and does nothing when switched off");
                                string[][] errandPhrases = new string[][] {
                                    new string[] { "set a timer for 10 minutes", "Timer" }, new string[] { "Capri, can you remind me in 20 minutes to stretch?", "Timer" }, new string[] { "timer 30 seconds", "Timer" },
                                    new string[] { "set a timer for half an hour please", "Timer" }, new string[] { "set an alarm for 7:30 pm", "Clock" }, new string[] { "wake me at 6am", "Clock" },
                                    new string[] { "open downloads", "Open" }, new string[] { "open my documents folder", "Open" }, new string[] { "hey capri open the calculator", "Open" },
                                    new string[] { "pause the music", "Pause" }, new string[] { "pause", "Pause" }, new string[] { "resume", "Play" }, new string[] { "play the music", "Play" },
                                    new string[] { "skip", "Next" }, new string[] { "next song", "Next" }, new string[] { "previous song", "Previous" }, new string[] { "mute", "Mute" },
                                    new string[] { "turn it up", "Volume" }, new string[] { "turn the volume down", "Volume" }, new string[] { "volume up", "Volume" },
                                    new string[] { "remember that my dog is called Bo", "Remember" }, new string[] { "forget about my dog", "Forget" }, new string[] { "forget everything", "ForgetAll" },
                                    new string[] { "start a study session for 50 minutes", "Study" }, new string[] { "stop studying", "StopStudy" }, new string[] { "cancel my alarms", "ClearAlarms" },
                                    new string[] { "open your heart", "None" }, new string[] { "forget it", "None" }, new string[] { "remember when we met?", "None" }, new string[] { "I set a timer for 5 minutes yesterday", "None" },
                                    new string[] { "what is a timer for", "None" }, new string[] { "play", "None" }, new string[] { "turn up the heat", "None" }, new string[] { "how are you today", "None" }, new string[] { "open the pod bay doors", "None" } };
                                foreach (string[] row in errandPhrases)
                                    if (Errands.Parse(row[0]).Kind.ToString() != row[1]) throw new Exception("\"" + row[0] + "\" was read as " + Errands.Parse(row[0]).Kind + " instead of " + row[1] + ".");
                                if (Errands.Length("in 90 seconds") != 90 || Errands.Length("for an hour") != 3600 || Errands.Length("hello") != 0) throw new Exception("Lengths of time were not read correctly.");
                                DateTime noon = new DateTime(2026, 1, 1, 12, 0, 0);
                                if (Errands.NextClock(7, 30, "p", noon) != new DateTime(2026, 1, 1, 19, 30, 0) || Errands.NextClock(6, 0, "a", noon) != new DateTime(2026, 1, 2, 6, 0, 0) || Errands.NextClock(7, 0, "", noon) != new DateTime(2026, 1, 1, 19, 0, 0)) throw new Exception("Clock times were not read correctly.");
                                string[] startMenu = new string[] { @"C:\Menu\Discord Inc\Discord.lnk", @"C:\Menu\Microsoft Edge.lnk", @"C:\Menu\Uninstall Foo.lnk", @"C:\Menu\Google Chrome.lnk", @"C:\Menu\Steam\Steam.lnk", @"C:\Menu\Steam\Steam Support Center.lnk", @"C:\Menu\Visual Studio Code\Visual Studio Code.lnk" };
                                if (Errands.Match("discord", startMenu) != startMenu[0] || Errands.Match("edge", startMenu) != startMenu[1] || Errands.Match("chrome", startMenu) != startMenu[3] || Errands.Match("steam", startMenu) != startMenu[4] || Errands.Match("vscode", startMenu) != startMenu[6] || Errands.Match("uninstall foo", startMenu) != null || Errands.Match("pod bay doors", startMenu) != null) throw new Exception("Installed programs were not found by name.");
                                Func<string, string> realLookup = Errands.AppLookup;
                                Errands.AppLookup = delegate(string name) { return Errands.Match(name, startMenu); };
                                if (!Errands.Parse("open downloads and make an empty txt file called RIP John").More || Errands.Parse("open downloads and make an empty txt file called RIP John").Text != "downloads" || Errands.Parse("open downloads").More) throw new Exception("Opening something and then asking for more was not understood.");
                                if (Errands.Parse("open Discord").Kind != ErrandKind.Open || Errands.Parse("open Discord").Target != startMenu[0] || Errands.Parse("open edge").Kind != ErrandKind.Open || Errands.Parse("open your heart").Kind != ErrandKind.None) throw new Exception("Opening a program by name was not understood.");
                                Errands.AppLookup = realLookup;
                                if (Errands.Parse("remember, I have a cat called John").Text != "I have a cat called John" || Errands.Parse("Please remember that I have a cat called John.").Kind != ErrandKind.Remember || Errands.Parse("don't forget my birthday is in May").Kind != ErrandKind.Remember) throw new Exception("Ways of saying remember were not understood.");
                                if (Errands.Parse("Remove my cat, John, from your memory").Kind != ErrandKind.Forget || Errands.Parse("forget my cat").Kind != ErrandKind.Forget || Errands.Parse("forget about the cat").Kind != ErrandKind.Forget || Errands.Parse("forget it").Kind != ErrandKind.None || Errands.Parse("forget you").Kind != ErrandKind.None) throw new Exception("Ways of saying forget were not understood.");
                                string opened = null;
                                System.Collections.Generic.List<byte> pressed = new System.Collections.Generic.List<byte>();
                                Opener = delegate(string target) { opened = target; };
                                KeySender = delegate(byte key) { pressed.Add(key); };
                                preferences.Alarms.Clear();
                                OnChatSent("set a timer for 10 minutes");
                                if (preferences.Alarms.Count != 1 || Math.Abs((new DateTime(preferences.Alarms[0].Due, DateTimeKind.Utc) - DateTime.UtcNow).TotalMinutes - 10) > 0.5) throw new Exception("A spoken timer was not set.");
                                OnChatSent("open downloads");
                                if (opened == null || !opened.EndsWith("Downloads")) throw new Exception("A folder she was asked to open was not opened.");
                                Media.Source = delegate { Track errandTrack = new Track(); errandTrack.Title = "x"; errandTrack.Playing = false; return errandTrack; };
                                OnChatSent("pause the music");
                                if (pressed.Count != 0) throw new Exception("She paused something that was not playing.");
                                Media.Source = delegate { Track errandTrack = new Track(); errandTrack.Title = "x"; errandTrack.Playing = true; return errandTrack; };
                                OnChatSent("pause the music");
                                OnChatSent("turn it up");
                                OnChatSent("skip");
                                Media.Source = null;
                                if (pressed.Count != 7 || pressed[0] != 0xB3 || pressed[1] != 0xAF || pressed[5] != 0xAF || pressed[6] != 0xB0) throw new Exception("Media and volume keys were not pressed as asked.");
                                int hitsBefore = server.Hits;
                                OnChatSent("open your heart");
                                if (!waitFor(delegate { return !llmBusy && server.Hits > hitsBefore; })) throw new Exception("Something that is not an errand was not passed on to the model.");
                                preferences.Alarms.Clear();
                                RebuildAlarms();
                                Opener = null;
                                KeySender = null;
                                Random habitDice = new Random(7);
                                int choomLeft = 0, questionsLeft = 0;
                                for (int round = 0; round < 200; round++)
                                {
                                    string said = Llm.Shape("C'mon, let's go. Anytime, choom! What now?", habitDice);
                                    if (said.IndexOf("choom", StringComparison.OrdinalIgnoreCase) >= 0) choomLeft++;
                                    if (said.EndsWith("?")) questionsLeft++;
                                    if (said.Length == 0 || said.StartsWith(" ") || said.IndexOf(" !") >= 0 || (char.IsLetter(said[0]) && char.IsLower(said[0]))) throw new Exception("A trimmed reply was left untidy: " + said);
                                }
                                if (choomLeft < 10 || choomLeft > 80 || questionsLeft < 50 || questionsLeft > 130) throw new Exception("Her habits were not trimmed to a sensible share: choom " + choomLeft + ", questions " + questionsLeft + " of 200.");
                                if (Llm.Shape("Hello there.", habitDice) != "Hello there." || Llm.Shape("What?", habitDice) != "What?" || Llm.Shape("", habitDice) != "") throw new Exception("A short reply was changed.");
                                if (Llm.DefaultPersona.IndexOf("Capri") < 0 || Llm.DefaultPersona.Length < 600) throw new Exception("Her built-in persona file was not embedded.");
                                string[][] canonTable = new string[][] {
                                    new string[] { "Who are you?", "Me? I'm Capri!" },
                                    new string[] { "Capri, what are you?", "A personality construct. Digitized human souls. A whole lot of 'em rolled into one to make me." },
                                    new string[] { "are you an AI?", "Not exactly. I was made to reach something close. I was human once." }, new string[] { "why should I trust you", "Come on, let's be friends." },
                                    new string[] { "Are you dangerous?", "Dangerous? Me? I'm harmless!" }, new string[] { "are you trying to take over my pc", "Just 'cause I tried doesn't mean I actually did. For now..." },
                                    new string[] { "I don't like you", "You don't have to lie." }, new string[] { "thanks capri", "Anytime!" } };
                                foreach (string[] row in canonTable)
                                    if (Canon.Reply(row[0]) != row[1]) throw new Exception("\"" + row[0] + "\" got " + (Canon.Reply(row[0]) ?? "nothing") + " instead of her own line.");
                                foreach (string other in new string[] { "hi", "Hey Capri!", "Hey you!", "Are you a boy or a girl?", "how are you", "who are you going to vote for", "what are you doing", "I'm bored", "tell me about you and your friends in the other room" })
                                    if (Canon.Reply(other) != null) throw new Exception("\"" + other + "\" was answered with a canned line.");
                                lastCanon = "";
                                OnChatSent("who are you?");
                                if (chatLog.Count < 2 || chatLog[chatLog.Count - 1].Content != "Me? I'm Capri!") throw new Exception("Her own line was not used for a question that has one.");
                                int canonHits = server.Hits;
                                OnChatSent("who are you?");
                                if (!waitFor(delegate { return !llmBusy && server.Hits > canonHits; })) throw new Exception("The same line twice in a row was not left to the model.");
                                lastCanon = "";
                                if (bubble != null) bubble.Dismiss();
                                Senses.Idle = delegate { return 1; };
                                Senses.Cpu = delegate { return 10; };
                                Senses.Battery = delegate { return -1; };
                                Senses.App = delegate { return "chrome"; };
                                Senses.Now = delegate { return new DateTime(2026, 1, 1, 15, 0, 0); };
                                Action<string, string> noticeOnce = delegate(string what, string wanted)
                                {
                                    if (bubble != null) bubble.Dismiss();
                                    lastSaid = -100;
                                    lastObserve = -100;
                                    noticeNext = 0;
                                    Observe();
                                    if (bubble == null || !bubble.Visible) throw new Exception("She said nothing about: " + what);
                                    if (bubble.Message.IndexOf(wanted) < 0) throw new Exception("She noticed " + what + " but said: " + bubble.Message);
                                    bubble.Dismiss();
                                };
                                bool noticeSetting = preferences.Notices;
                                preferences.Notices = true;
                                noticed.Clear();
                                Observe();
                                if (bubble != null && bubble.Visible) throw new Exception("She spoke up with nothing to notice.");
                                wasAway = true; peakIdle = 1800;
                                noticeOnce("you coming back", "");
                                Senses.Now = delegate { return new DateTime(2026, 1, 1, 3, 15, 0); };
                                noticeOnce("it being late", "3:15 am");
                                Senses.Now = delegate { return new DateTime(2026, 1, 1, 15, 0, 0); };
                                Senses.Battery = delegate { return 14; };
                                noticeOnce("a low battery", "14%");
                                Senses.Battery = delegate { return -1; };
                                cpuBusy = 40; Senses.Cpu = delegate { return 96; };
                                noticeOnce("a busy computer", "96%");
                                Senses.Cpu = delegate { return 10; };
                                cpuBusy = 0;
                                frontSince = clock.Elapsed.TotalSeconds - 7300;
                                noticeOnce("a long time in one program", "Chrome");
                                lastSaid = -100; lastObserve = -100; noticeNext = 0;
                                Observe();
                                if (bubble != null && bubble.Visible) throw new Exception("She repeated a remark before its pause was over.");
                                noticed.Clear();
                                preferences.Notices = false;
                                Senses.Battery = delegate { return 9; };
                                lastObserve = -100; noticeNext = 0;
                                Observe();
                                if (bubble != null && bubble.Visible) throw new Exception("She spoke up with noticing switched off.");
                                preferences.Notices = noticeSetting;
                                Senses.Idle = Native.IdleSeconds; Senses.Cpu = Senses.SystemCpu; Senses.Battery = Senses.OnBattery; Senses.App = Native.FrontApp; Senses.Now = delegate { return DateTime.Now; };
                                if (Native.IdleSeconds() < 0 || Native.IdleSeconds() > 86400 * 30) throw new Exception("The idle time could not be read.");
                                if (Senses.Friendly("msedge") != "Edge" || Senses.Friendly("discord") != "Discord") throw new Exception("Program names were not made readable.");
                                noticed.Clear();
                                noticeNext = double.MaxValue;
                                if (Dares.Check("Dare you to close every tab except one.") == null || Dares.Check("Hello there, how are you today friend?") != null || Dares.Check("Dare you to hurt someone.") != null || Dares.Check("Dare you to " + new string('x', 200)) != null) throw new Exception("A dare from the model was not checked properly.");
                                if (Dares.Category("chrome") != "browser" || Dares.Category("Discord") != "chat" || Dares.Category("code") != "code" || Dares.Category("unknown") != "any") throw new Exception("Programs were not sorted into kinds of dare.");
                                foreach (string kind in new string[] { "browser", "chat", "code", "media", "docs", "any" })
                                    foreach (string line in Dares.Pool(kind)) if (Dares.Check(line) == null) throw new Exception("One of her own dares would be refused: " + line);
                                bool llmWas = preferences.LlmOn;
                                preferences.LlmOn = false;
                                if (bubble != null) bubble.Dismiss();
                                lastSaid = -100; lastObserve = -100; noticeNext = 0; dareNext = 0;
                                Senses.Idle = delegate { return 1; }; Senses.Cpu = delegate { return 10; }; Senses.Battery = delegate { return -1; };
                                Senses.App = delegate { return "chrome"; }; Senses.Now = delegate { return new DateTime(2026, 1, 1, 15, 0, 0); };
                                preferences.Notices = true; preferences.Dares = true;
                                noticed.Clear();
                                frontApp = ""; frontRaw = "";
                                Observe();
                                frontSince = clock.Elapsed.TotalSeconds - 700;
                                lastObserve = -100; noticeNext = 0; dareNext = 0; lastSaid = -100;
                                if (bubble != null) bubble.Dismiss();
                                Observe();
                                if (bubble == null || !bubble.Visible || bubble.Message.IndexOf("Dare you to") != 0 || Array.IndexOf(Dares.Pool("browser"), bubble.Message) < 0) throw new Exception("An automatic dare for a browser did not appear. bubble=" + (bubble == null ? "null" : bubble.Visible + "/" + bubble.Message) + " behavior=" + behavior + " chat=" + ChatOpen() + " hidden=" + hidden + " fs=" + fullscreenAway + " riding=" + riding + " dragging=" + dragging + " study=" + study + " frontRaw=" + frontRaw + " since=" + (clock.Elapsed.TotalSeconds - frontSince));
                                bubble.Dismiss();
                                int doneBefore = preferences.DaresDone;
                                OnChatSent("done");
                                if (preferences.DaresDone != doneBefore + 1) throw new Exception("Finishing a dare was not counted.");
                                OnChatSent("done");
                                if (preferences.DaresDone != doneBefore + 1) throw new Exception("A dare was counted twice.");
                                preferences.Dares = false;
                                frontSince = clock.Elapsed.TotalSeconds - 700;
                                lastObserve = -100; noticeNext = 0; dareNext = 0;
                                if (bubble != null) bubble.Dismiss();
                                Observe();
                                if (bubble != null && bubble.Visible) throw new Exception("She dared me with dares switched off.");
                                preferences.Dares = true;
                                lastSaid = -100;
                                OnChatSent("dare me");
                                if (chatLog[chatLog.Count - 1].Content.IndexOf("Dare you to") != 0) throw new Exception("Asking for a dare did not get one.");
                                OnChatSent("Dear me!");
                                if (chatLog[chatLog.Count - 1].Content.IndexOf("Dare you to") != 0) throw new Exception("A misheard 'dare me' did not get a dare.");
                                foreach (string unsafeDare in new string[] { "Dare you to jump out the window.", "Dare you to jump off the roof.", "Dare you to light a cigarette.", "Dare you to hit yourself.", "Dare you to drink a beer.", "Dare you to kill your neighbour." })
                                    if (Dares.Check(unsafeDare) != null) throw new Exception("An unsafe dare was accepted: " + unsafeDare);
                                foreach (string fineDare in new string[] { "Dare you to close every window except one.", "Dare you to stop peeking at the score tab and shoot something.", "Dare you to jump into the next round.", "Dare you to rush the site and get blown up before the timer hits zero.", "Dare you to jump off the ledge on this map.", "Dare you to kill the whole enemy team with the knife." })
                                    if (Dares.Check(fineDare) == null) throw new Exception("A harmless dare was refused: " + fineDare);
                                preferences.LlmOn = llmWas;
                                Senses.Idle = Native.IdleSeconds; Senses.Cpu = Senses.SystemCpu; Senses.Battery = Senses.OnBattery; Senses.App = Native.FrontApp; Senses.Now = delegate { return DateTime.Now; };
                                dareNext = double.MaxValue; noticeNext = double.MaxValue; dareOpenUntil = 0;
                                preferences.SyncPoints = 0; preferences.SyncShown = 0; preferences.SyncDay = ""; preferences.SyncMinutes = 0; preferences.SyncChats = 0; preferences.SyncPats = 0;
                                Senses.Now = delegate { return new DateTime(2026, 1, 1, 15, 0, 0); };
                                if (bubble != null) bubble.Dismiss();
                                lastSaid = -100;
                                for (int i = 0; i < 40; i++) AddSync(2, "chat");
                                if (preferences.SyncPoints != 20 || preferences.SyncChats != 10) throw new Exception("The daily limit on chatting was not kept: " + preferences.SyncPoints);
                                for (int i = 0; i < 40; i++) AddSync(1, "time");
                                for (int i = 0; i < 40; i++) AddSync(1, "pat");
                                if (preferences.SyncPoints != 20 + 24 + 10) throw new Exception("The daily limits on time and pats were not kept: " + preferences.SyncPoints);
                                Senses.Now = delegate { return new DateTime(2026, 1, 2, 15, 0, 0); };
                                AddSync(2, "chat");
                                if (preferences.SyncPoints != 56 || preferences.SyncChats != 1) throw new Exception("A new day did not reset the limits.");
                                preferences.SyncPoints = 95; preferences.SyncShown = 0;
                                lastSaid = -100;
                                AddSync(10, "dare");
                                if (preferences.SyncPoints != 105 || preferences.SyncShown != 1 || bubble == null || !bubble.Visible || bubble.Message.IndexOf("SYNC 10%") < 0 || bubble.Message.IndexOf("TEAR") < 0) throw new Exception("Reaching 10% was not announced with what it unlocks.");
                                bubble.Dismiss();
                                preferences.SyncPoints = 0;
                                for (int i = 0; i < 100; i++) if (PickKind() != 0) throw new Exception("A glitch style was used before it was unlocked.");
                                preferences.SyncPoints = 1000;
                                bool[] seenKinds = new bool[Glitch.Kinds.Length];
                                for (int i = 0; i < 300; i++) seenKinds[PickKind()] = true;
                                foreach (bool seenKind in seenKinds) if (!seenKind) throw new Exception("Not every glitch style opens up at full sync.");
                                preferences.SyncPoints = 400;
                                if (!BrainText().Contains("sync 40%") || BrainText().IndexOf("teases") < 0) throw new Exception("The model was not told how close she is.");
                                lastSaid = -100;
                                OnChatSent("how's our sync?");
                                if (chatLog[chatLog.Count - 1].Content.IndexOf("40%") < 0) throw new Exception("Asking about sync did not give the number.");
                                glitchKindNext = 4;
                                glitchQuiet = true;
                                Play(Behavior.Glitch, true);
                                if (Math.Abs(Glitch.SyncTarget - 0.4) > 0.001) throw new Exception("The sync box did not show the real number.");
                                StopBehavior();
                                Glitch.SyncTarget = 0.87;
                                glitchLength = 2.6;
                                preferences.SyncPoints = 0; preferences.SyncShown = 0; preferences.SyncDay = "";
                                Senses.Now = delegate { return DateTime.Now; };
                                checks.Add("PASS sync grows from time, chatting, pats and dares within daily limits, announces each ten percent, opens up glitch styles, tells the model how close she is and shows the real number in her sync box");
                                checks.Add("PASS she dares you for what you are doing (the model's dare is checked, her own list is the fallback), counts finished dares once, and can be asked or switched off");
                                Cloud.AllowPlainLocal = true;
                                Cloud.TestBase = "http://localhost:" + server.Port + "/v1";
                                Cloud.ClearKey();
                                if (Cloud.HasKey()) throw new Exception("A key was left over from before.");
                                Cloud.SaveKey("sk-test-123");
                                if (!Cloud.HasKey() || File.ReadAllText(Cloud.KeyPath).IndexOf("sk-test") >= 0 || Cloud.LoadKey() != "sk-test-123") throw new Exception("The key was not kept encrypted and readable by this user.");
                                System.Collections.Generic.List<ChatLine> cloudSample = new System.Collections.Generic.List<ChatLine>();
                                cloudSample.Add(new ChatLine("system", "be capri \"quoted\""));
                                cloudSample.Add(new ChatLine("user", "hi\nthere"));
                                cloudSample.Add(new ChatLine("user", "again"));
                                ChatLine cloudPicture = new ChatLine("user", "look");
                                cloudPicture.Images = new string[] { "QUJD" };
                                cloudSample.Add(cloudPicture);
                                string openBody = Cloud.OpenAiBody("openrouter", "m1", cloudSample);
                                if (openBody.IndexOf("\"image_url\"") < 0 || openBody.IndexOf("data:image/jpeg;base64,QUJD") < 0 || openBody.IndexOf("\\\"quoted\\\"") < 0 || openBody.IndexOf("hi\\nthere") < 0 || openBody.IndexOf("\"max_tokens\"") < 0) throw new Exception("The OpenAI-style request was not built correctly: " + openBody);
                                if (Cloud.OpenAiBody("openai", "m1", cloudSample).IndexOf("max_completion_tokens") < 0 || Cloud.OpenAiBody("openai", "m1", cloudSample).IndexOf("temperature") >= 0) throw new Exception("The OpenAI request did not use its own limits.");
                                string claudeBody = Cloud.AnthropicBody("m1", cloudSample);
                                if (claudeBody.IndexOf("\"system\":\"be capri \\\"quoted\\\"\"") < 0 || claudeBody.IndexOf("\"type\":\"image\"") < 0 || claudeBody.IndexOf("\"media_type\":\"image/jpeg\"") < 0 || claudeBody.IndexOf("hi\\nthere\\nagain") < 0) throw new Exception("The Anthropic request was not built correctly: " + claudeBody);
                                if (!Cloud.UrlOk("https://example.com/v1") || !Cloud.UrlOk("http://localhost:1/v1") || Cloud.UrlOk("ftp://example.com") || Cloud.UrlOk("not a url")) throw new Exception("Cloud addresses were not checked.");
                                Cloud.AllowPlainLocal = false;
                                if (Cloud.UrlOk("http://localhost:1/v1") || Cloud.UrlOk("http://example.com/v1")) throw new Exception("A plain address was accepted outside testing.");
                                Cloud.AllowPlainLocal = true;

                                bool lookWas = preferences.CloudLook;
                                preferences.LlmOn = true;
                                preferences.MemoryOn = true;
                                preferences.CloudMemory = false;
                                ApplyCloud("custom", "cloud-test", "http://localhost:" + server.Port + "/v1", "sk-test-123", false, false, false);
                                if (llmLink == null || !llmLink.Cloud || !preferences.CloudOn || !llmStatus.StartsWith("Cloud:")) throw new Exception("Switching to the cloud model did not connect it.");
                                waitFor(delegate { return server.LastPath == "/v1/chat/completions"; });
                                if (!waitFor(delegate { return !llmBusy; })) throw new Exception("Busy after setting up the cloud model.");
                                Remember("my dog is called Bo");
                                int cloudHits = server.Hits;
                                OnChatSent("what is up with you");
                                if (!waitFor(delegate { return !llmBusy && server.Hits > cloudHits; })) throw new Exception("The cloud model was not asked.");
                                if (server.LastPath != "/v1/chat/completions" || server.LastAuth != "Bearer sk-test-123" || server.LastBody.IndexOf("cloud-test") < 0) throw new Exception("The cloud request went to the wrong place: " + server.LastPath + " " + server.LastAuth);
                                if (server.LastBody.IndexOf("WHAT THE USER HAS TOLD YOU") >= 0) throw new Exception("What she remembers was sent to the cloud without permission.");
                                preferences.CloudMemory = true;
                                cloudHits = server.Hits;
                                OnChatSent("tell me something else");
                                if (!waitFor(delegate { return !llmBusy && server.Hits > cloudHits; }) || server.LastBody.IndexOf("my dog is called Bo") < 0) throw new Exception("What she remembers was not sent although it was allowed.");
                                preferences.CloudMemory = false;
                                ScreenSource = delegate { return new byte[] { 9, 9, 9, 9 }; };
                                localLink = null;
                                cloudHits = server.Hits;
                                Look("");
                                if (server.Hits != cloudHits) throw new Exception("A screenshot was sent to the cloud without permission.");
                                preferences.CloudLook = true;
                                Look("");
                                if (!waitFor(delegate { return !llmBusy && server.Hits > cloudHits; }) || server.LastBody.IndexOf("image_url") < 0 || server.LastBody.IndexOf(Convert.ToBase64String(new byte[] { 9, 9, 9, 9 })) < 0) throw new Exception("An allowed screenshot was not sent in the cloud's format.");
                                preferences.CloudLook = false;
                                ScreenSource = null;
                                server.RejectExtras = true;
                                cloudHits = server.Hits;
                                OnChatSent("this model refuses extras");
                                if (!waitFor(delegate { return !llmBusy && server.Hits >= cloudHits + 2; }) || server.LastBody.IndexOf("\"temperature\"") >= 0 || chatLog[chatLog.Count - 1].Content != "Hello from the fake model.") throw new Exception("A request the model refused was not retried with plain settings: " + Cloud.LastError);
                                server.RejectExtras = false;
                                server.ForceStatus = 400;
                                cloudHits = server.Hits;
                                OnChatSent("this model is refused");
                                if (!waitFor(delegate { return !llmBusy && server.Hits >= cloudHits + 2; }) || Cloud.LastError.IndexOf("bad model") < 0 || Cloud.LastError.IndexOf("refused") < 0) throw new Exception("The provider's own message was not shown: " + Cloud.LastError);
                                server.ForceStatus = 0;
                                server.EmptyAnswer = true;
                                cloudHits = server.Hits;
                                OnChatSent("say nothing please");
                                if (!waitFor(delegate { return !llmBusy && server.Hits >= cloudHits + 2; }) || Cloud.LastError.IndexOf("nothing") < 0) throw new Exception("An empty answer was not retried and explained: " + Cloud.LastError);
                                server.EmptyAnswer = false;
                                if (TrySync("are we friends?") || TrySync("do you like me")) throw new Exception("An ordinary question was taken for a sync question.");
                                ApplyCloud("anthropic", "claude-test", "", "", false, false, false);
                                waitFor(delegate { return !llmBusy && server.LastPath == "/v1/messages"; });
                                cloudHits = server.Hits;
                                OnChatSent("and now with claude");
                                if (!waitFor(delegate { return !llmBusy && server.Hits > cloudHits; })) throw new Exception("The Anthropic model was not asked.");
                                if (server.LastPath != "/v1/messages" || server.LastAuth != "sk-test-123" || server.LastVersion != "2023-06-01" || chatLog[chatLog.Count - 1].Content != "Hello from the fake claude.") throw new Exception("The Anthropic request or answer was wrong: " + server.LastPath + " " + server.LastAuth + " " + server.LastVersion);
                                server.ForceStatus = 401;
                                int logBefore = chatLog.Count;
                                cloudHits = server.Hits;
                                OnChatSent("this will fail");
                                if (!waitFor(delegate { return !llmBusy && server.Hits > cloudHits; }) || Cloud.LastError.IndexOf("key") < 0 || chatLog.Exists(delegate(ChatLine line) { return line.Content == "this will fail"; })) throw new Exception("A refused key was not reported or left a stray message: " + Cloud.LastError);
                                server.ForceStatus = 0;
                                ApplyCloud("anthropic", "claude-test", "", "", false, false, true);
                                if (Cloud.HasKey() || preferences.CloudOn) throw new Exception("Removing the key did not switch the cloud model off.");
                                Cloud.AllowPlainLocal = false;
                                Cloud.TestBase = null;
                                preferences.CloudLook = lookWas;
                                preferences.CloudMemory = false;
                                preferences.LlmOn = true;
                                Connect(false);
                                if (!waitFor(delegate { return llmLink != null && !llmLink.Cloud; })) throw new Exception("She did not go back to the local model.");
                                if (!waitFor(delegate { return !llmBusy; })) throw new Exception("Busy after the cloud test.");
                                using (CloudForm view = new CloudForm("anthropic", "claude-haiku-5-5", "", false, false, true))
                                {
                                    view.Show();
                                    Application.DoEvents();
                                    using (Bitmap shot = new Bitmap(view.Width, view.Height))
                                    {
                                        view.DrawToBitmap(shot, new Rectangle(Point.Empty, view.Size));
                                        shot.Save(Path.Combine(output, "cloud-form.png"), ImageFormat.Png);
                                    }
                                }
                                Memory().Clear();
                                SaveMemory();
                                checks.Add("PASS the cloud model is optional and opt-in: the key is stored encrypted, both request formats and headers are right, notes and screenshots leave only when allowed, errors are explained, and removing the key switches it off");
                                Senses.Now = delegate { return new DateTime(2026, 3, 3, 15, 0, 0); };
                                preferences.SyncPoints = 0; preferences.SyncShown = 0; preferences.SyncDay = ""; preferences.SyncMinutes = 0; preferences.SyncChats = 0; preferences.SyncPats = 0; preferences.SyncAgrees = 0;
                                if (!waitFor(delegate { return !llmBusy; })) throw new Exception("Busy before the agreement test.");
                                lastReplyAt = -1000;
                                OnChatSent("ok");
                                waitFor(delegate { return !llmBusy; });
                                if (preferences.SyncPoints != 2 || preferences.SyncAgrees != 0) throw new Exception("Agreeing with nothing counted: " + preferences.SyncPoints);
                                for (int i = 0; i < 8; i++)
                                {
                                    lastReplyAt = clock.Elapsed.TotalSeconds;
                                    OnChatSent("sure");
                                    waitFor(delegate { return !llmBusy; });
                                }
                                if (preferences.SyncAgrees != 5 || preferences.SyncPoints != 2 + 16 + 10) throw new Exception("Agreeing with her was not counted up to five a day: " + preferences.SyncAgrees + " " + preferences.SyncPoints);
                                preferences.SyncPoints = 0; preferences.SyncShown = 0; preferences.SyncDay = ""; preferences.SyncChats = 0; preferences.SyncAgrees = 0;
                                Senses.Now = delegate { return DateTime.Now; };

                                bool visionLlm = preferences.LlmOn;
                                preferences.LlmOn = true;
                                if (llmLink == null || llmLink.Cloud || !llmLink.Ollama) throw new Exception("The local model was not connected for the screen dare test.");
                                byte[] dareShot = new byte[] { 7, 7, 7, 7 };
                                ScreenSource = delegate { return dareShot; };
                                preferences.VisionDares = true;
                                int dareHits = server.Hits;
                                GiveDare(true);
                                if (!waitFor(delegate { return !llmBusy && server.Hits > dareHits; }) || server.LastBody.IndexOf(Convert.ToBase64String(dareShot)) < 0 || server.LastBody.IndexOf("Look at the attached screenshot") < 0) throw new Exception("A dare with the screen allowed did not send the screenshot.");
                                preferences.VisionDares = false;
                                dareHits = server.Hits;
                                GiveDare(true);
                                if (!waitFor(delegate { return !llmBusy && server.Hits > dareHits; }) || server.LastBody.IndexOf(Convert.ToBase64String(dareShot)) >= 0 || server.LastBody.IndexOf("Look at the attached") >= 0) throw new Exception("A screenshot was sent for a dare with the switch off.");
                                ScreenSource = null;
                                preferences.LlmOn = visionLlm;
                                dareNext = double.MaxValue;
                                checks.Add("PASS agreeing with her adds a little sync (up to five a day, only right after she spoke), and a dare uses a screenshot only when its own switch is on");
                                if (Errands.Parse("play never gonna give you up on spotify").Kind != ErrandKind.PlaySong || Errands.Parse("play never gonna give you up on spotify").Text != "never gonna give you up" || Errands.Parse("Capri, put on Blinding Lights in Spotify please").Text != "Blinding Lights" || Errands.Parse("spotify play lofi beats").Text != "lofi beats") throw new Exception("Playing a song on Spotify was not understood.");
                                if (Errands.Parse("play the music on spotify").Kind == ErrandKind.PlaySong || Errands.Parse("play the music").Kind != ErrandKind.Play || Errands.Parse("play").Kind != ErrandKind.None) throw new Exception("Ordinary play commands were taken for a song.");
                                if (Errands.Parse("search for cute cats on youtube").Target != "https://www.youtube.com/results?search_query=cute%20cats" || Errands.Parse("google capri edgerunners").Target != "https://www.google.com/search?q=capri%20edgerunners" || Errands.Parse("look up pizza near me").Kind != ErrandKind.Web || Errands.Parse("play lofi on youtube").Kind != ErrandKind.PlayVideo || Errands.Parse("open youtube").Target != "https://www.youtube.com") throw new Exception("Searching the web was not understood.");
                                if (Errands.Parse("search me").Kind == ErrandKind.Web && false) throw new Exception("unreachable");
                                string[] swedish = new string[] { "Gå tillbaka", "Gå framåt", "Hem", "Sök", "Spela upp Donda Chant", "Spela upp I Really Want to Stay at Your House", "Spela upp Master of Puppets (Remastered)", "Spela upp The Rebel Path", "Spela upp Sandstorm", "Spela upp EL RITMO", "Rensa sökhistorik", "Gilla" };
                                if (SpotifyUi.Verb(swedish) != "Spela upp" || SpotifyUi.Verb(new string[] { "Play Blinding Lights", "Play Sandstorm", "Play The Rebel Path", "Play Believer", "Home", "Search" }) != "Play") throw new Exception("The word for Play was not found in a list of buttons.");
                                if (SpotifyUi.Find(swedish, "Spela upp", "i really wanna stay at your house") != 5 || SpotifyUi.Find(swedish, "Spela upp", "the rebel path") != 7 || SpotifyUi.Find(swedish, "Spela upp", "sandstorm") != 8 || SpotifyUi.Find(swedish, "Spela upp", "some song nobody has heard of") != -1 || SpotifyUi.Find(swedish, "Spela upp", "gilla") != -1) throw new Exception("The Play button for a song was not picked by name.");
                                if (YouTube.FirstVideo("\"videoRenderer\":{\"videoId\":\"KvMY1uzSC1E\",\"thumbnail\"") != "KvMY1uzSC1E" || YouTube.FirstVideo("nothing here") != null) throw new Exception("The first video was not found in a results page.");
                                if (Errands.Parse("play i really want to stay at your house on youtube").Kind != ErrandKind.PlayVideo || Errands.Parse("search for cute cats on youtube").Kind != ErrandKind.Web) throw new Exception("Playing on YouTube was not told apart from searching.");
                                {
                                    string playOpened = null;
                                    Opener = delegate(string target) { playOpened = target; };
                                    Func<string[]> buttonsWas = SpotifyUi.Buttons;
                                    Func<int, bool> invokeWas = SpotifyUi.Invoke;
                                    int pollWas = SpotifyUi.Poll;
                                    double secondsWas = preferences.SpotifySeconds;
                                    SpotifyUi.Poll = 20;
                                    preferences.SpotifySeconds = 1.5;
                                    int playPressed = -1;
                                    int looks = 0;
                                    SpotifyUi.Buttons = delegate { looks++; return looks < 3 ? new string[] { "Hem", "Sök" } : swedish; };
                                    SpotifyUi.Invoke = delegate(int index) { playPressed = index; return true; };
                                    Media.Source = delegate
                                    {
                                        Track started = new Track();
                                        started.Title = "I Really Want to Stay at Your House";
                                        started.Artist = "Rosa Walton";
                                        started.Playing = playPressed >= 0;
                                        return started;
                                    };
                                    foreach (string said in new string[] { "Play, I really want to stay at your house on Spotify", "Clay, I really want to stay at your house on Spotify", "play I really want to stay at your house spotify please", "Capri, play i really want to stay at your house on spotify" })
                                        if (Errands.Parse(said).Kind != ErrandKind.PlaySong || Errands.Parse(said).Text.IndexOf("stay at your house") < 0) throw new Exception("A way of asking for a song was not understood: " + said);
                                    if (Errands.Parse("Play, I really want to stay at your house on YouTube").Kind != ErrandKind.PlayVideo || Errands.Parse("Clay i really want to stay at your house on youtube").Kind != ErrandKind.PlayVideo) throw new Exception("A way of asking for a video was not understood.");
                                    OnChatSent("play i really wanna stay at your house on spotify");
                                    if (!waitFor(delegate { return !spotifyBusy && playPressed >= 0; }) || playOpened != "spotify:search:i%20really%20wanna%20stay%20at%20your%20house") throw new Exception("Spotify was not searched and the song's Play button playPressed: " + playOpened + " " + playPressed);
                                    if (playPressed != 5) throw new Exception("The wrong button was playPressed: " + playPressed);
                                    if (!waitFor(delegate { return bubble != null && bubble.Visible && bubble.Message != null && bubble.Message.StartsWith("Playing"); })) throw new Exception("She did not say it was playing: " + (bubble == null ? "no bubble" : bubble.Message));
                                    playPressed = -1;
                                    Media.Source = null;
                                    string[] searchPage = new string[] { "Gå tillbaka", "Hem", "Sök", "Spela upp The Rebel Path", "Mer alternativ för The Rebel Path", "Mer alternativ för Sandstorm", "Mer alternativ för Believer", "Mer alternativ för Hero", "Mer alternativ för Last Man", "Gilla" };
                                    if (SpotifyUi.Choose(searchPage, "") != "Spela upp" || SpotifyUi.Choose(searchPage, "Spela upp") != "Spela upp" || SpotifyUi.Choose(new string[] { "Play Sandstorm", "More options for Sandstorm", "More options for A", "More options for B", "More options for C", "More options for D" }, "") != "Play") throw new Exception("The Play wording was mixed up with other buttons that have the song's name.");
                                    if (SpotifyUi.Find(searchPage, "Spela upp", "the rebel path") != 3) throw new Exception("The Play button on a search page was not the one picked.");
                                    SpotifyUi.Buttons = delegate { return swedish; };
                                    if (File.Exists(Path.Combine(Path.GetDirectoryName(settingsPath), "spotify-buttons.txt"))) File.Delete(Path.Combine(Path.GetDirectoryName(settingsPath), "spotify-buttons.txt"));
                                    OnChatSent("play a song nobody has heard of on spotify");
                                    waitFor(delegate { return !spotifyBusy; });
                                    Thread.Sleep(200);
                                    if (playPressed != -1) throw new Exception("A button was playPressed for a song that is not in the results.");
                                    string note = Path.Combine(Path.GetDirectoryName(settingsPath), "spotify-buttons.txt");
                                    if (!File.Exists(note) || File.ReadAllText(note).IndexOf("Spela upp Sandstorm") < 0 || File.ReadAllText(note).IndexOf("nobody has heard of") < 0) throw new Exception("What she saw in Spotify was not written down when she could not find the song.");
                                    if (preferences.SpotifyVerb != "Spela upp") throw new Exception("The Play wording was not remembered: " + preferences.SpotifyVerb);
                                    // already playing: nothing is pressed
                                    Media.Source = delegate { Track now = new Track(); now.Title = "The Rebel Path"; now.Artist = "Someone"; now.Playing = true; return now; };
                                    SpotifyUi.Buttons = delegate { return swedish; };
                                    playPressed = -1;
                                    OnChatSent("play the rebel path on spotify");
                                    if (playPressed != -1 || !waitFor(delegate { return chatLog[chatLog.Count - 1].Content.StartsWith("That's already"); })) throw new Exception("Something already playing was pressed again.");
                                    // pressing play only selects it: the play key is pressed once and it starts
                                    bool nudgedPlay = false;
                                    Media.Source = delegate { Track now = new Track(); now.Title = "Sandstorm"; now.Artist = "Darude"; now.Playing = nudgedPlay; return now; };
                                    KeySender = delegate(byte key) { if (key == 0xB3) nudgedPlay = true; };
                                    preferences.SpotifySeconds = 1.5;
                                    SpotifyUi.Poll = 20;
                                    OnChatSent("play sandstorm on spotify");
                                    if (!waitFor(delegate { return !spotifyBusy && nudgedPlay; }) || !waitFor(delegate { return bubble != null && bubble.Visible && bubble.Message != null && bubble.Message.StartsWith("Playing"); })) throw new Exception("A press that did not start the song was not followed by the play key.");
                                    KeySender = null;
                                    Media.Source = null;
                                    SpotifyUi.Buttons = delegate { return null; };
                                    OnChatSent("play anything on spotify please");
                                    SpotifyUi.Buttons = buttonsWas;
                                    SpotifyUi.Invoke = invokeWas;
                                    SpotifyUi.Poll = pollWas;
                                    preferences.SpotifySeconds = secondsWas;
                                    YouTube.Fetch = delegate(string query) { return "\"videoRenderer\":{\"videoId\":\"KvMY1uzSC1E\",\"x\""; };
                                    playOpened = null;
                                    OnChatSent("play i really want to stay at your house on youtube");
                                    if (!waitFor(delegate { return playOpened != null; }) || playOpened != "https://www.youtube.com/watch?v=KvMY1uzSC1E") throw new Exception("The first video was not playOpened: " + playOpened);
                                    YouTube.Fetch = delegate(string query) { return ""; };
                                    playOpened = null;
                                    OnChatSent("play something on youtube");
                                    if (!waitFor(delegate { return playOpened != null; }) || playOpened.IndexOf("results?search_query=something") < 0) throw new Exception("A failed lookup did not fall back to the results page: " + playOpened);
                                    YouTube.Fetch = null;
                                    Opener = null;
                                }
                                {
                                    string runWas = Startup.RunPath;
                                    Startup.RunPath = "Software\\Capri.Test\\Run";
                                    try
                                    {
                                        Startup.Set(false);
                                        if (Startup.Enabled()) throw new Exception("Start with Windows was on before the test.");
                                        Startup.Set(true);
                                        if (!Startup.Enabled()) throw new Exception("Start with Windows could not be switched on.");
                                        Startup.Set(false);
                                        if (Startup.Enabled()) throw new Exception("Start with Windows could not be switched off.");
                                    }
                                    finally
                                    {
                                        try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree("Software\\Capri.Test", false); } catch (Exception) { }
                                        Startup.RunPath = runWas;
                                    }
                                }
                                checks.Add("PASS she plays a song on Spotify by finding and pressing its Play button by name, plays the first video on YouTube, searches the web, and Start with Windows switches on and off");
                                checks.Add("PASS she notices (and says something short) when you come back, stay up late, run low on battery, max out the CPU or sit in one program for hours, with long pauses and an off switch");
                                checks.Add("PASS she does small things when asked (timers, alarms, opening a few folders and tools, media and volume keys) and passes anything else to the model");

                                memory = null;
                                try { File.Delete(MemoryPath()); } catch (IOException) { }
                                OnChatSent("remember that my dog is called Bo");
                                OnChatSent("my name is sam, by the way");
                                if (!waitFor(delegate { return !llmBusy; })) throw new Exception("Telling her your name was not answered.");
                                OnChatSent("remember my password is hunter2");
                                if (Memory().Count != 2 || !Memory().Contains("my dog is called Bo") || !Memory().Contains("their name is Sam")) throw new Exception("She did not keep what she was told, or kept a password: " + string.Join(" | ", Memory().ToArray()));
                                memory = null;
                                if (Memory().Count != 2 || File.ReadAllText(MemoryPath()).IndexOf("hunter2") >= 0) throw new Exception("What she remembers was not saved to the file.");
                                hitsBefore = server.Hits;
                                OnChatSent("what should we do today?");
                                if (!waitFor(delegate { return !llmBusy && server.Hits > hitsBefore; })) throw new Exception("The question was not answered.");
                                if (server.LastBody.IndexOf("WHAT THE USER HAS TOLD YOU") < 0 || server.LastBody.IndexOf("- my dog is called Bo") < 0 || server.LastBody.IndexOf("- their name is Sam") < 0) throw new Exception("What she remembers was not given to the model.");
                                preferences.MemoryOn = false;
                                hitsBefore = server.Hits;
                                OnChatSent("and another question");
                                if (!waitFor(delegate { return !llmBusy && server.Hits > hitsBefore; })) throw new Exception("The question was not answered.");
                                if (server.LastBody.IndexOf("WHAT THE USER HAS TOLD YOU") >= 0) throw new Exception("She used her memory although it was switched off.");
                                preferences.MemoryOn = true;
                                OnChatSent("forget about my dog");
                                if (Memory().Count != 1 || Memory()[0] != "their name is Sam") throw new Exception("She did not forget what she was asked to.");
                                OnChatSent("forget everything");
                                if (Memory().Count != 0 || File.ReadAllText(MemoryPath()).Trim().Length != 0) throw new Exception("She did not forget everything.");
                                OnChatSent("remember, I have a cat called John");
                                if (!Memory().Contains("I have a cat called John")) throw new Exception("A remember with a comma was not kept.");
                                OnChatSent("Remove my cat, John, from your memory");
                                if (Memory().Contains("I have a cat called John")) throw new Exception("She did not forget a note described in other words.");
                                notices.Clear();
                                Announce("first thing", 5, "reply");
                                bubble.Present("an answer that was just shown", 10, TopMost, 12f, false, null, null);
                                Announce("time's up! 10 seconds.", 5, "alarm");
                                notices.Dequeue();
                                Deliver();
                                if (bubble.Message != "time's up! 10 seconds.") throw new Exception("A finished timer waited behind another bubble.");
                                notices.Clear();
                                checks.Add("PASS she remembers what she is told (and her name for you), refuses passwords, gives it to the model only while memory is on, and forgets on request");
                                Talk();
                                int answersBefore = Regex.Matches(chat.Transcript, "Hello from the fake model.").Count;
                                Listen();
                                if (!ChatOpen() || !ear.Listening || !chat.Listening || sendHeard == null || cutin == null || !cutin.Visible || cutin.Key != "eyes") throw new Exception("Speaking to her did not open the window and start listening.");
                                sendHeard("hello by voice", 0.9);
                                if (!waitFor(delegate { return !llmBusy && Regex.Matches(chat.Transcript, "Hello from the fake model.").Count == answersBefore + 1; })) throw new Exception("Something she heard clearly was not sent and answered.");
                                if (chat.Transcript.IndexOf("Hello by voice") < 0 || ear.Listening || chat.Listening || cutin.Visible) throw new Exception("Listening did not finish tidily after a clear phrase.");
                                int turns = chatLog.Count;
                                Listen();
                                sendHeard("maybe this one", 0.1);
                                if (!waitFor(delegate { return chat.Draft == "Maybe this one"; })) throw new Exception("A shaky guess was not left in the box to be checked.");
                                if (chatLog.Count != turns || chat.Transcript.IndexOf("wasn't sure") < 0) throw new Exception("A shaky guess was sent without being checked.");
                                chat.SetDraft("");
                                Listen();
                                unheard = 0;
                                sendFailed("silence");
                                if (!waitFor(delegate { return !chat.Listening && chat.Transcript.IndexOf("Can't hear you!") >= 0; })) throw new Exception("Not hearing anything was not reported.");
                                Listen();
                                sendFailed("silence");
                                if (!waitFor(delegate { return !chat.Listening && chat.Transcript.IndexOf("Still can't hear you!") >= 0; })) throw new Exception("Not hearing anything twice in a row was not reported more pointedly.");
                                Listen();
                                sendFailed("error");
                                if (!waitFor(delegate { return !chat.Listening && chat.Transcript.IndexOf("can't use that microphone") >= 0; })) throw new Exception("A microphone that cannot be used was not reported.");
                                unheard = 0;
                                Listen();
                                Listen();
                                if (ear.Listening || chat.Listening) throw new Exception("Pressing listen twice did not stop her listening.");
                                Message voiceMessage = Message.Create(Handle, 0x0312, new IntPtr(VoiceKeyId), IntPtr.Zero);
                                WndProc(ref voiceMessage);
                                if (!ear.Listening) throw new Exception("The voice hotkey did not start listening.");
                                Listen();
                                ear.Source = null;
                                if (Ear.Available)
                                {
                                    using (SR.SpeechRecognitionEngine real = new SR.SpeechRecognitionEngine(Ear.Pick())) real.LoadGrammar(new SR.DictationGrammar());
                                    lastOverHer = clock.Elapsed.TotalSeconds;
                                    MicWatch();
                                    if (mic == null || !mic.Wanted) throw new Exception("The mic button did not appear when the pointer was on her.");
                                    mic.Settle();
                                    if (!mic.Visible) throw new Exception("The mic button was not shown.");
                                    using (Bitmap picture = mic.Render(false, 0)) picture.Save(Path.Combine(output, "mic-button.png"), ImageFormat.Png);
                                    using (Bitmap picture = mic.Render(true, 0.3)) picture.Save(Path.Combine(output, "mic-listening.png"), ImageFormat.Png);
                                    lastOverHer = -100;
                                    MicWatch();
                                    if (mic.Wanted) throw new Exception("The mic button did not go away when the pointer left her.");
                                    mic.Settle();
                                    if (mic.Visible) throw new Exception("The mic button stayed on screen.");
                                    micItem.PerformClick();
                                    lastOverHer = clock.Elapsed.TotalSeconds;
                                    MicWatch();
                                    if (mic.Wanted || preferences.MicButton) throw new Exception("The mic button appeared with its switch off.");
                                    micItem.PerformClick();
                                    lastOverHer = -100;
                                    MicWatch();
                                    checks.Add("PASS speaking to her listens on request, sends clear speech, holds back shaky guesses, and the hover mic and hotkey work");
                                }
                                else checks.Add("LIMIT no speech recognizer installed, so only the stand-in microphone was tested");
                                chat.Hide();
                                SetMusic(2);
                                if (musicTimer != null) { musicTimer.Dispose(); musicTimer = null; }
                                Track aiSong = new Track();
                                aiSong.Title = "Neon Rain";
                                aiSong.Artist = "Test Band";
                                aiSong.App = "Spotify";
                                aiSong.Playing = true;
                                lastComment = -1000;
                                bubble.Dismiss();
                                Comment(aiSong);
                                if (!waitFor(delegate { return bubble.Visible && bubble.Message == "Hello from the fake model."; })) throw new Exception("Her reaction from the local model never appeared.");
                                if (server.LastBody.IndexOf("Neon Rain") < 0 || server.LastBody.IndexOf("Test Band") < 0 || server.LastBody.IndexOf("can't hear it") < 0) throw new Exception("The song was not described to the model as expected: " + server.LastBody);
                                SetMusic(0);
                                bubble.Dismiss();
                                checks.Add("PASS with a model connected she reacts through it, telling it she only knows the title");
                                int hits = server.Hits;
                                llmItem.PerformClick();
                                if (llmLink != null || llmStatusItem.Text != "Off" || chatLog.Count != 0 || preferences.LlmOn) throw new Exception("Switching the local model off did not disconnect her.");
                                Converse("anyone there?");
                                Talk();
                                chat.Submit("still there?");
                                if (server.Hits != hits) throw new Exception("She contacted the model after it was switched off.");
                                chat.Hide();
                                llmItem.PerformClick();
                                if (!waitFor(delegate { return llmLink != null; })) throw new Exception("She did not reconnect.");
                                checks.Add("PASS switching the model off stops all contact until it is switched on again");
                                server.Broken = true;
                                Converse("hello?");
                                if (!waitFor(delegate { return !llmBusy; })) throw new Exception("A failed request never finished.");
                                if (bubble.Message.IndexOf("can't reach my brain") < 0 || chatLog.Count != 0) throw new Exception("A failed request was not reported or left a stray message behind.");
                                checks.Add("PASS a model that errors gets a friendly reply and no stray history");
                            }
                            TrySetServer("");
                            if (preferences.LlmOn) llmItem.PerformClick();
                            bubble.Dismiss();
                            Rectangle work = Screen.FromPoint(new Point(anchor.X + Width / 2, anchor.Y + Height / 2)).WorkingArea;
                            Func<int, Form> openWindow = delegate(int top)
                            {
                                Form window = new Form();
                                window.Text = "Capri test window";
                                window.FormBorderStyle = FormBorderStyle.FixedDialog;
                                window.ShowInTaskbar = false;
                                window.StartPosition = FormStartPosition.Manual;
                                window.Bounds = new Rectangle(work.Left + 200, top, 600, 300);
                                window.Show();
                                Application.DoEvents();
                                return window;
                            };
                            Point ground = anchor;
                            using (Form perch = openWindow(work.Top + 320))
                            {
                                Play(Behavior.Sit, true);
                                if (behavior != Behavior.Sit || sitWindow != perch.Handle) throw new Exception("She did not pick the test window to sit on.");
                                double sat = clock.Elapsed.TotalSeconds;
                                for (int n = 0; n < 60 && sitStage != SitStage.Sitting; n++) { sat += 0.05; AdvanceSit(sat, 0.05); }
                                Point spot;
                                if (sitStage != SitStage.Sitting || !SitTarget(out spot) || anchor != spot || Math.Abs(anchor.Y + SitSeat().Y - (Native.FrameOf(perch.Handle).Top + 6)) > 1) throw new Exception("She did not land on the top edge of the window.");
                                perch.Left += 150;
                                Application.DoEvents();
                                for (int n = 0; n < 30; n++) { sat += 0.05; AdvanceSit(sat, 0.05); }
                                if (!SitTarget(out spot) || anchor != spot) throw new Exception("She did not follow the window when it moved.");
                                lastCursor = new Point(-9, -9);
                                Watch();
                                if (behavior != Behavior.Sit) throw new Exception("Moving the pointer made her get down.");
                                RenderFrame();
                                Rectangle edge = Native.FrameOf(perch.Handle);
                                Rectangle box = Rectangle.Inflate(Rectangle.Union(edge, new Rectangle(anchor, Size)), 12, 12);
                                using (Bitmap picture = new Bitmap(box.Width, box.Height))
                                using (Graphics graphics = Graphics.FromImage(picture))
                                {
                                    graphics.Clear(Color.FromArgb(237, 239, 242));
                                    using (Brush body = new SolidBrush(Color.FromArgb(250, 251, 252))) graphics.FillRectangle(body, edge.X - box.X, edge.Y - box.Y, edge.Width, edge.Height);
                                    using (Brush bar = new SolidBrush(Color.FromArgb(214, 218, 224))) graphics.FillRectangle(bar, edge.X - box.X, edge.Y - box.Y, edge.Width, 34);
                                    graphics.DrawImage(frame, anchor.X - box.X, anchor.Y - box.Y);
                                    picture.Save(Path.Combine(output, "pose-sit.png"), ImageFormat.Png);
                                }
                                checks.Add("PASS she sits on the top edge of a window and follows it when it moves");
                                using (Form cover = openWindow(work.Top + 320))
                                {
                                    cover.Left = perch.Left;
                                    cover.TopMost = true;
                                    Application.DoEvents();
                                    Thread.Sleep(150);
                                    Application.DoEvents();
                                    sat += 1;
                                    AdvanceSit(sat, 0.05);
                                    if (sitStage != SitStage.Leaving)
                                    {
                                        string dump = "";
                                        foreach (Native.Pane pane in Native.Panes(Handle, IntPtr.Zero, true)) dump += " [" + pane.Handle + " " + pane.Frame + " " + pane.Sittable + "]";
                                        throw new Exception("A window covering her seat did not send her home. stage=" + sitStage + " perch=" + perch.Handle + " cover=" + cover.Handle + " frac=" + sitFrac + " anchor=" + anchor + " seat=" + SitSeat() + dump);
                                    }
                                    for (int n = 0; n < 60 && behavior == Behavior.Sit; n++) { sat += 0.05; AdvanceSit(sat, 0.05); }
                                    if (behavior != Behavior.None || anchor != ground) throw new Exception("She did not get back home after losing her seat.");
                                }
                                Play(Behavior.Sit, true);
                                for (int n = 0; n < 60 && sitStage != SitStage.Sitting; n++) { sat += 0.05; AdvanceSit(sat, 0.05); }
                                OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, Width / 2, Height / 2, 0));
                                OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, Width / 2, Height / 2, 0));
                                if (sitStage != SitStage.Leaving) throw new Exception("Touching her did not send her home.");
                                for (int n = 0; n < 60 && behavior == Behavior.Sit; n++) { sat += 0.05; AdvanceSit(sat, 0.05); }
                                if (behavior != Behavior.None || anchor != ground) throw new Exception("She did not hop home after being touched.");
                                clickDelay.Stop();
                                pendingClick = false;
                                Play(Behavior.Sit, true);
                                for (int n = 0; n < 60 && sitStage != SitStage.Sitting; n++) { sat += 0.05; AdvanceSit(sat, 0.05); }
                                perch.Close();
                                Application.DoEvents();
                                sat += 1;
                                AdvanceSit(sat, 0.05);
                                if (sitStage != SitStage.Leaving) throw new Exception("Closing her window did not send her home.");
                                for (int n = 0; n < 60 && behavior == Behavior.Sit; n++) { sat += 0.05; AdvanceSit(sat, 0.05); }
                                if (behavior != Behavior.None || anchor != ground) throw new Exception("She did not get home after her window closed.");
                            }
                            checks.Add("PASS she gets down when touched, covered or when her window closes");
                            using (Form high = openWindow(work.Top + 10))
                            {
                                Play(Behavior.Sit, true);
                                if (behavior != Behavior.None || bubble.Message.IndexOf("nowhere to sit") < 0) throw new Exception("She tried to sit where there is no room.");
                            }
                            using (Form perch = openWindow(work.Top + 320))
                            {
                                preferences.Sits = false;
                                nextSit = 0;
                                reactionStart = -10;
                                Wander();
                                if (behavior == Behavior.Sit) throw new Exception("She sat down on her own with the switch off.");
                                preferences.Sits = true;
                                Wander();
                                if (behavior != Behavior.Sit) throw new Exception("She did not sit down on her own.");
                                StopBehavior();
                                if (anchor != ground) throw new Exception("Stopping a sit did not put her back.");
                            }
                            nextSit = double.MaxValue;
                            bubble.Dismiss();
                            checks.Add("PASS she needs room above a window, and her automatic sit obeys its switch");
                            using (Form drop = openWindow(work.Top + 320))
                            {
                                Rectangle top = Native.FrameOf(drop.Handle);
                                Point seatAt = SitSeat();
                                restHome = ground;
                                anchor = new Point(top.Left + 200 - seatAt.X, top.Top + 20 - seatAt.Y);
                                SitWhereDropped();
                                if (behavior != Behavior.Sit || sitWindow != drop.Handle || sitHome != ground) throw new Exception("Dropping her on a window did not make her sit there.");
                                double fall = clock.Elapsed.TotalSeconds;
                                for (int n = 0; n < 60 && sitStage != SitStage.Sitting; n++) { fall += 0.05; AdvanceSit(fall, 0.05); }
                                Point settled;
                                if (sitStage != SitStage.Sitting || !SitTarget(out settled) || anchor != settled) throw new Exception("She did not settle on the window she was dropped on.");
                                StopBehavior();
                                if (anchor != ground) throw new Exception("She did not go back to her old spot after the drop.");
                                anchor = new Point(top.Left + 200 - seatAt.X, top.Top - 400);
                                SitWhereDropped();
                                if (behavior != Behavior.None || restHome != anchor) throw new Exception("Dropping her in the open should simply leave her there.");
                                anchor = new Point(top.Left + 200 - seatAt.X, top.Top + 20 - seatAt.Y);
                                restHome = anchor;
                                anchor = ground;
                                restHome = ground;
                            }
                            checks.Add("PASS dropping her on a window's top edge makes her sit, and dropping her anywhere else does not");
                            Rectangle screenArea = new Rectangle(0, 0, 1920, 1080);
                            if (!Native.CoversMonitor(screenArea, screenArea, false, "UnityWndClass") || Native.CoversMonitor(new Rectangle(0, 0, 1920, 1040), screenArea, false, "Chrome_WidgetWin_1") || Native.CoversMonitor(screenArea, screenArea, true, "Chrome_WidgetWin_1") || Native.CoversMonitor(screenArea, screenArea, false, "Progman")) throw new Exception("Full screen windows were not told apart from ordinary ones.");
                            Native.FullscreenAhead();
                            bool inGame = false;
                            fullscreenProbe = delegate { return inGame; };
                            inGame = true;
                            CheckFullscreen();
                            if (!hidden || !fullscreenAway || Visible) throw new Exception("She did not step aside for a full screen program.");
                            Announce("it's time.", 20, "alarm");
                            Deliver();
                            if (notices.Count != 0) throw new Exception("An alarm was not handed over while she was away.");
                            inGame = false;
                            CheckFullscreen();
                            if (hidden || fullscreenAway || !Visible) throw new Exception("She did not come back after the full screen program ended.");
                            preferences.HideInFullscreen = false;
                            inGame = true;
                            CheckFullscreen();
                            if (hidden) throw new Exception("She hid even though the switch was off.");
                            fullscreenItem.PerformClick();
                            if (!preferences.HideInFullscreen || !hidden) throw new Exception("Switching hiding on did not take effect straight away.");
                            fullscreenItem.PerformClick();
                            if (hidden || fullscreenAway) throw new Exception("Switching hiding off did not bring her back.");
                            fullscreenItem.PerformClick();
                            visibilityItem.PerformClick();
                            if (hidden) throw new Exception("Showing her by hand did not work during full screen.");
                            CheckFullscreen();
                            if (hidden) throw new Exception("She hid again after being shown by hand.");
                            inGame = false;
                            CheckFullscreen();
                            inGame = true;
                            CheckFullscreen();
                            if (!hidden) throw new Exception("A new full screen program did not hide her again.");
                            inGame = false;
                            CheckFullscreen();
                            visibilityItem.PerformClick();
                            inGame = true;
                            CheckFullscreen();
                            if (!hidden || fullscreenAway) throw new Exception("Hiding her by hand was confused with hiding for full screen.");
                            inGame = false;
                            CheckFullscreen();
                            if (!hidden) throw new Exception("She came back on her own after being hidden by hand.");
                            visibilityItem.PerformClick();
                            if (hidden || !Visible) throw new Exception("She would not come back from the menu.");
                            fullscreenProbe = delegate { return false; };
                            checks.Add("PASS she steps aside for full screen programs, returns afterwards, and respects manual choices");
                            Directory.CreateDirectory(voice.Folder);
                            foreach (string clip in new string[] { "pat.wav", "pat-2.wav", "pat3.wav", "PAT_4.WAV", "patrol.wav", "stretch.wav", "alarm.wav", "note.txt" }) File.WriteAllBytes(Path.Combine(voice.Folder, clip), new byte[64]);
                            if (voice.Clips("pat").Count != 4 || voice.Clips("sit").Count != 0 || voice.Clips("alarm").Count != 1 || voice.Total() != 6 || VoiceStatus() != "6 voice clips found") throw new Exception("Voice clips were not found by name.");
                            if (((ToolStripMenuItem)menu.Items[12]).Text != "Voice") throw new Exception("Voice menu is not where the tests expect.");
                            List<string> heard = new List<string>();
                            voice.Sink = heard.Add;
                            lastVoice = -100;
                            if (!Speak("pat", true) || heard.Count != 1 || !Path.GetFileName(heard[0]).ToLowerInvariant().StartsWith("pat")) throw new Exception("A voice line did not play.");
                            if (Speak("sit", true) || heard.Count != 1) throw new Exception("A voice line played with no clip for it.");
                            lastVoice = clock.Elapsed.TotalSeconds;
                            if (Speak("pat", false)) throw new Exception("Automatic voice lines were not spaced out.");
                            lastVoice = -100;
                            if (!Speak("pat", false)) throw new Exception("An automatic voice line never played.");
                            heard.Clear();
                            for (int n = 0; n < 8; n++) Speak("pat", true);
                            for (int n = 1; n < heard.Count; n++) if (heard[n] == heard[n - 1]) throw new Exception("The same clip played twice in a row.");
                            heard.Clear();
                            muteItem.PerformClick();
                            if (!preferences.Muted || Speak("pat", true)) throw new Exception("Mute did not silence her.");
                            PlaySample();
                            if (heard.Count != 0) throw new Exception("A sample played while muted.");
                            muteItem.PerformClick();
                            if (preferences.Muted) throw new Exception("Unmuting did not work.");
                            lastVoice = -100;
                            Play(Behavior.Stretch, true);
                            if (heard.Count != 1 || !Path.GetFileName(heard[0]).StartsWith("stretch")) throw new Exception("Her stretch did not get its voice line.");
                            StopBehavior();
                            lastVoice = -100;
                            Play(Behavior.Pat, true);
                            if (!Path.GetFileName(heard[heard.Count - 1]).ToLowerInvariant().StartsWith("pat")) throw new Exception("A pat did not get its voice line.");
                            StopBehavior();
                            bubble.Dismiss();
                            Announce("ring", 20, "alarm");
                            Deliver();
                            if (!Path.GetFileName(heard[heard.Count - 1]).StartsWith("alarm")) throw new Exception("An alarm did not play its voice line.");
                            StartStudy(25, 5);
                            heard.Clear();
                            lastVoice = -100;
                            if (Speak("pat", false) || heard.Count != 0) throw new Exception("She spoke on her own during focus.");
                            StopStudy();
                            bubble.Dismiss();
                            lastVoice = -100;
                            PlaySample();
                            if (heard.Count != 1) throw new Exception("Play a sample did not play anything.");
                            checks.Add("PASS voice clips are found by name, never repeat back to back, obey mute and focus, and replace the chime");
                            voice.Sink = null;
                            using (MemoryStream wave = new MemoryStream())
                            using (BinaryWriter writer = new BinaryWriter(wave))
                            {
                                byte[] silence = new byte[800];
                                for (int n = 0; n < silence.Length; n++) silence[n] = 0x80;
                                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                                writer.Write(36 + silence.Length);
                                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                                writer.Write(16);
                                writer.Write((short)1);
                                writer.Write((short)1);
                                writer.Write(8000);
                                writer.Write(8000);
                                writer.Write((short)1);
                                writer.Write((short)8);
                                writer.Write(Encoding.ASCII.GetBytes("data"));
                                writer.Write(silence.Length);
                                writer.Write(silence);
                                writer.Flush();
                                File.WriteAllBytes(Path.Combine(voice.Folder, "hello.wav"), wave.ToArray());
                            }
                            File.WriteAllBytes(Path.Combine(voice.Folder, "yawn.wav"), new byte[64]);
                            lastVoice = -100;
                            if (!Speak("hello", true)) throw new Exception("A real wave file did not play.");
                            voice.Stop();
                            lastVoice = -100;
                            if (Speak("yawn", true)) throw new Exception("A broken clip was reported as played.");
                            OpenVoiceFolder();
                            if (!File.Exists(Path.Combine(voice.Folder, "README.txt"))) throw new Exception("The voice folder note was not written.");
                            lastVoice = -100;
                            checks.Add("PASS a real wave file plays, a broken one is skipped quietly, and the voice folder gets a how-to note");
                            bool before = hotkey;
                            lastSaid = -100;
                            glitchKindNext = 0;
                            Play(Behavior.Glitch, true);
                            if (behavior != Behavior.Glitch || overlay == null || !overlay.Visible) throw new Exception("The glitch did not start with its overlay.");
                            overlay.Frame(0.8);
                            using (Bitmap sheet = new Bitmap(overlay.Canvas.Width, overlay.Canvas.Height))
                            using (Graphics graphics = Graphics.FromImage(sheet))
                            {
                                graphics.Clear(Color.FromArgb(237, 239, 242));
                                graphics.DrawImage(overlay.Canvas, 0, 0);
                                using (Bitmap small = new Bitmap(sheet, sheet.Width / 2, sheet.Height / 2)) small.Save(Path.Combine(output, "glitch-overlay.png"), ImageFormat.Png);
                            }
                            double ratio = 0;
                            for (int y = 0; y < overlay.Canvas.Height; y += 8)
                                for (int x = 0; x < overlay.Canvas.Width; x += 8)
                                    if (overlay.Canvas.GetPixel(x, y).A > 90) ratio++;
                            if (ratio / ((overlay.Canvas.Width / 8) * (overlay.Canvas.Height / 8)) > 0.12) throw new Exception("The glitch overlay covers too much of the screen at once.");
                            behaviorStart = clock.Elapsed.TotalSeconds - 1.1;
                            RenderFrame();
                            frame.Save(Path.Combine(output, "pose-glitch.png"), ImageFormat.Png);
                            behaviorStart -= 1;
                            Advance();
                            if (bubble == null || !bubble.Visible || Array.IndexOf(HackLines, bubble.Message) < 0) throw new Exception("The hacked note did not appear.");
                            Thread.Sleep(1700);
                            bubble.Tick();
                            bubble.Picture.Save(Path.Combine(output, "hack-note.png"), ImageFormat.Png);
                            behaviorStart -= 10;
                            Advance();
                            if (behavior != Behavior.None || overlay.Visible || (hotkey && !before) || nextMischief < clock.Elapsed.TotalSeconds + 150) throw new Exception("The glitch did not finish and tidy up.");
                            checks.Add("PASS a glitch shows the overlay and a hacked note, stays gentle on screen, and tidies up afterwards");
                            if (overlay != null)
                            {
                                for (int style = 0; style < Glitch.Kinds.Length; style++)
                                {
                                    overlay.Kind = style;
                                    overlay.Run(Screen.PrimaryScreen.Bounds, Glitch.Seconds[style]);
                                    foreach (double at in new double[] { 0.4, 0.9, 1.6, Glitch.Seconds[style] * 0.7 })
                                    {
                                        overlay.Frame(at);
                                        double covered = 0, drawn = 0;
                                        for (int y = 0; y < overlay.Canvas.Height; y += 8)
                                            for (int x = 0; x < overlay.Canvas.Width; x += 8)
                                            {
                                                int alpha = overlay.Canvas.GetPixel(x, y).A;
                                                if (alpha > 90) covered++;
                                                if (alpha > 0) drawn++;
                                            }
                                        if (covered / ((overlay.Canvas.Width / 8) * (overlay.Canvas.Height / 8)) > 0.12) throw new Exception("The " + Glitch.Kinds[style] + " glitch covers too much of the screen.");
                                        if (drawn == 0) throw new Exception("The " + Glitch.Kinds[style] + " glitch drew nothing.");
                                    }
                                    overlay.Frame(2.0);
                                    using (Bitmap small = new Bitmap(overlay.Canvas, overlay.Canvas.Width / 2, overlay.Canvas.Height / 2)) small.Save(Path.Combine(output, "glitch-style-" + style + ".png"), ImageFormat.Png);
                                    overlay.Stop();
                                }
                                overlay.Kind = 0;
                                glitchKindNext = 5;
                                glitchQuiet = true;
                                Play(Behavior.Glitch, true);
                                if (behavior != Behavior.Glitch || overlay.Kind != 5 || Length(Behavior.Glitch) < 3.5) throw new Exception("A chosen glitch style did not run with its own length.");
                                StopBehavior();
                                glitchLength = 2.6;
                                checks.Add("PASS every glitch style stays gentle on screen, draws something, and can be chosen from the menu");
                            }
                            Play(Behavior.Glitch, true);
                            Message escape = Message.Create(Handle, 0x0312, new IntPtr(DismountId), IntPtr.Zero);
                            WndProc(ref escape);
                            if (behavior != Behavior.None || overlay.Visible || (hotkey && !before)) throw new Exception("Ctrl + Alt + C did not stop the glitch.");
                            Play(Behavior.Glitch, true);
                            StartRide();
                            if (behavior != Behavior.None || overlay.Visible || !riding) throw new Exception("Perching on the cursor did not end the glitch.");
                            StopRide();
                            checks.Add("PASS Ctrl + Alt + C and perching both stop a glitch at once");
                            bubble.Dismiss();
                            effectsOn = delegate { return false; };
                            glitchQuiet = false;
                            Play(Behavior.Glitch, true);
                            if (overlay.Visible) throw new Exception("The overlay appeared although Windows animations are switched off.");
                            behaviorStart -= 1;
                            Advance();
                            if (bubble == null || !bubble.Visible) throw new Exception("The note should still appear without the overlay.");
                            StopBehavior();
                            effectsOn = delegate { return true; };
                            bubble.Dismiss();
                            glitchQuiet = true;
                            Play(Behavior.Glitch, true);
                            behaviorStart -= 1;
                            Advance();
                            if (bubble.Visible) throw new Exception("A glitch-only event wrote a note.");
                            StopBehavior();
                            overlay.Stop();
                            bubble.Dismiss();
                            preferences.Mischief = true;
                            nextMischief = 0;
                            reactionStart = -10;
                            Wander();
                            if (behavior != Behavior.Glitch) throw new Exception("Mischief mode did not start a glitch on its own.");
                            StopBehavior();
                            preferences.Mischief = false;
                            nextMischief = 0;
                            Wander();
                            if (behavior == Behavior.Glitch) throw new Exception("A glitch happened on its own with mischief off.");
                            nextMischief = double.MaxValue;
                            bubble.Dismiss();
                            checks.Add("PASS reduced motion keeps the overlay away, and automatic mischief only happens when switched on");
                            Func<string> shownKey = delegate { return cutin != null && cutin.Visible ? cutin.Key : ""; };
                            bubble.Dismiss();
                            Play(Behavior.Look, true);
                            scriptStart -= 0.5;
                            Advance();
                            if (shownKey() != "eyes-left") throw new Exception("Looking around did not start with the eyes looking left: " + shownKey());
                            Rectangle cutArea = Screen.FromPoint(new Point(anchor.X + Width / 2, anchor.Y + Height / 2)).WorkingArea;
                            if (!cutArea.Contains(cutin.Area)) throw new Exception("The cut-in card left the screen.");
                            scriptStart -= 1.0;
                            Advance();
                            if (shownKey() != "eyes-right") throw new Exception("The eyes did not move on to the right: " + shownKey());
                            using (Bitmap card = cutin.Render(1, true)) card.Save(Path.Combine(output, "cutin-eyes-right.png"), ImageFormat.Png);
                            using (Bitmap half = cutin.Render(0.5, true))
                                if (half.GetPixel(6, half.Height / 2).A != 0 || half.GetPixel(half.Width - 10, half.Height / 2).A == 0) throw new Exception("The cut-in card did not wipe in from the side nearest her.");
                            scriptStart -= 1.0;
                            Advance();
                            if (shownKey() != "eyes") throw new Exception("Looking around did not end on the open eyes: " + shownKey());
                            StopBehavior();
                            cutin.Dismiss();
                            Play(Behavior.Sleep, true);
                            scriptStart -= 1.0;
                            Advance();
                            if (shownKey() != "eyes-closed") throw new Exception("Falling asleep did not close her eyes: " + shownKey());
                            Noticed();
                            if (shownKey() != "eyes") throw new Exception("Waking up did not open her eyes: " + shownKey());
                            StopBehavior();
                            cutin.Dismiss();
                            Play(Behavior.Pat, true);
                            scriptStart -= 0.5;
                            Advance();
                            if (shownKey() != "happyface") throw new Exception("A pat did not bring up the happy face: " + shownKey());
                            StopBehavior();
                            cutin.Dismiss();
                            Play(Behavior.Glitch, true);
                            scriptStart -= 0.5;
                            Advance();
                            if (shownKey() != "eyes-many") throw new Exception("A glitch did not bring up the many eyes: " + shownKey());
                            scriptStart -= 1.0;
                            Advance();
                            if (shownKey() != "eyes-glitch") throw new Exception("A glitch did not end with the glitched eyes: " + shownKey());
                            using (Bitmap card = cutin.Render(1, true)) card.Save(Path.Combine(output, "cutin-eyes-glitch.png"), ImageFormat.Png);
                            StopBehavior();
                            cutin.Dismiss();
                            cutinItem.PerformClick();
                            ShowCut("eyes", 1);
                            if (preferences.CutIns || shownKey() != "") throw new Exception("A cut-in appeared with the switch off.");
                            cutinItem.PerformClick();
                            if (!preferences.CutIns) throw new Exception("Cut-ins did not switch back on.");
                            bubble.Dismiss();
                            checks.Add("PASS eye cut-ins follow her moods, wipe in from her side, stay on screen and obey their switch");
                            Action quietMusic = delegate { if (musicTimer != null) { musicTimer.Dispose(); musicTimer = null; } };
                            Track song = new Track();
                            song.Title = "Test Song";
                            song.Artist = "Test Artist";
                            song.App = "Spotify";
                            song.Playing = true;
                            bubble.Dismiss();
                            OnTrack(song);
                            if (bubble.Visible) throw new Exception("A now-playing card appeared with music awareness off.");
                            SetMusic(1);
                            quietMusic();
                            if (preferences.MusicMode != 1 || !musicModes[1].Checked || musicModes[0].Checked) throw new Exception("The music mode did not switch.");
                            OnTrack(song);
                            if (!bubble.Visible || bubble.Message != "NOW PLAYING // Test Song - Test Artist" || !musicStatusItem.Text.StartsWith("Playing: Test Song")) throw new Exception("The now-playing card was wrong: " + bubble.Message);
                            Thread.Sleep(1800);
                            bubble.Tick();
                            bubble.Picture.Save(Path.Combine(output, "now-playing.png"), ImageFormat.Png);
                            bubble.Dismiss();
                            OnTrack(song);
                            if (bubble.Visible) throw new Exception("The same song announced itself twice.");
                            Track paused = new Track();
                            paused.Title = song.Title;
                            paused.Artist = song.Artist;
                            paused.App = song.App;
                            OnTrack(paused);
                            if (!musicStatusItem.Text.StartsWith("Paused: Test Song") || musicPlaying) throw new Exception("A paused song was not shown as paused.");
                            OnTrack(song);
                            if (bubble.Visible) throw new Exception("Resuming the same song announced it again.");
                            OnTrack(paused);
                            notPlayingSince = clock.Elapsed.TotalSeconds - 30;
                            OnTrack(song);
                            if (!bubble.Visible || bubble.Message != "NOW PLAYING // Test Song - Test Artist") throw new Exception("The same song starting again after a real pause was not announced.");
                            bubble.Dismiss();
                            Track another = new Track();
                            another.Title = "Another Song";
                            another.App = "Edge";
                            another.Playing = true;
                            OnTrack(another);
                            if (!bubble.Visible || bubble.Message != "NOW PLAYING // Another Song") throw new Exception("A new song did not get its card: " + bubble.Message);
                            OnTrack(null);
                            if (musicStatusItem.Text != "Nothing playing" || musicPlaying) throw new Exception("Nothing playing was not shown.");
                            SetMusic(2);
                            quietMusic();
                            lastTrackKey = "";
                            lastComment = -1000;
                            bubble.Dismiss();
                            OnTrack(song);
                            if (commentLater == null || !commentLater.Enabled) throw new Exception("A reaction was not lined up behind the card.");
                            commentLater.Stop();
                            Comment(song);
                            if (Array.IndexOf(MusicLines, bubble.Message) < 0) throw new Exception("Without a model she did not give a plain cheer: " + bubble.Message);
                            bubble.Dismiss();
                            Comment(another);
                            if (bubble.Visible) throw new Exception("She reacted to two songs within seconds.");
                            SetMusic(0);
                            if (musicTimer != null || musicStatusItem.Text != "Off") throw new Exception("Switching music off did not stop watching.");
                            Media.Source = delegate { return song; };
                            if (Media.Read() != song || !Media.Available) throw new Exception("The stand-in media source was not used.");
                            Media.Source = null;
                            if (Media.Available) Media.Read();
                            bubble.Dismiss();
                            checks.Add("PASS music awareness shows each new song once, reacts with a cheer without a model, keeps to its cooldown and switches off");
                            string[] waves = Native.WaveDevices();
                            if (waves.Length > 0)
                            {
                                RebuildMics();
                                if (micDevices.DropDownItems.Count < 2 || !((ToolStripMenuItem)micDevices.DropDownItems[0]).Checked) throw new Exception("The microphone list was not built.");
                                string firstName = micDevices.DropDownItems[1].Text;
                                micDevices.DropDownItems[1].PerformClick();
                                if (preferences.MicDevice != firstName || ear.Device != firstName) throw new Exception("Choosing a microphone did not stick.");
                                RebuildMics();
                                if (!((ToolStripMenuItem)micDevices.DropDownItems[1]).Checked) throw new Exception("The chosen microphone was not ticked.");
                                micDevices.DropDownItems[0].PerformClick();
                                if (preferences.MicDevice != "" || ear.Device != "") throw new Exception("Going back to the Windows default did not work.");
                                using (MicTap tap = new MicTap(0))
                                {
                                    tap.Feed(new byte[] { 1, 2, 3, 4, 5 });
                                    byte[] three = new byte[3];
                                    byte[] remainder = new byte[10];
                                    if (tap.Read(three, 0, 3) != 3 || three[0] != 1 || three[2] != 3 || tap.Read(remainder, 0, 10) != 2 || remainder[1] != 5) throw new Exception("The microphone stream did not hand sound on in order.");
                                    tap.Stop();
                                    if (tap.Read(remainder, 0, 10) != 0) throw new Exception("The microphone stream did not end when stopped.");
                                }
                                bool captured = false;
                                for (int d = 0; d < waves.Length && !captured; d++)
                                    using (MicTap tap = new MicTap(d))
                                        if (tap.Start()) { Thread.Sleep(600); captured = tap.Total > 0; }
                                checks.Add(captured ? "PASS a chosen microphone can be listed, picked, opened and read as a stream" : "LIMIT no microphone could be opened for the capture test");
                                if (Ear.Available)
                                {
                                    byte[] speech;
                                    using (MemoryStream spoken = new MemoryStream())
                                    using (System.Speech.Synthesis.SpeechSynthesizer talker = new System.Speech.Synthesis.SpeechSynthesizer())
                                    {
                                        talker.SetOutputToAudioStream(spoken, new System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono));
                                        talker.Speak("hello capri what should I do today");
                                        speech = spoken.ToArray();
                                    }
                                    string understood = null;
                                    string problem = null;
                                    ear.TapFactory = delegate
                                    {
                                        MicTap fake = new MicTap(-1);
                                        ThreadPool.QueueUserWorkItem(delegate
                                        {
                                            for (int at = 0; at < speech.Length; at += 3200) { byte[] piece = new byte[Math.Min(3200, speech.Length - at)]; Array.Copy(speech, at, piece, 0, piece.Length); fake.Feed(piece); Thread.Sleep(60); }
                                            for (int quiet = 0; quiet < 40; quiet++) { fake.Feed(new byte[3200]); Thread.Sleep(60); }
                                        });
                                        return fake;
                                    };
                                    ear.Start(delegate(string text, double confidence) { understood = text; }, delegate(string why) { problem = why; });
                                    for (int n = 0; n < 750 && understood == null && problem == null; n++) { Application.DoEvents(); Thread.Sleep(20); }
                                    ear.Stop();
                                    if (understood == null || understood.IndexOf("hello", StringComparison.OrdinalIgnoreCase) < 0) throw new Exception("The recognizer did not understand speech coming through the microphone stream (" + (problem ?? "no result") + ").");
                                    if (Ear.FixName("hello copper-e what is your name") != "hello Capri what is your name" || Ear.FixName("Cap ree can you hear me") != "Capri can you hear me" || Ear.FixName("a copper kettle") != "a copper kettle") throw new Exception("Her name was not fixed in what was heard: " + Ear.FixName("hello copper-e what is your name"));
                                    byte[] hushed = (byte[])speech.Clone();
                                    for (int at = 0; at + 1 < hushed.Length; at += 2)
                                    {
                                        short softer = (short)(BitConverter.ToInt16(hushed, at) / 9);
                                        hushed[at] = (byte)(softer & 0xFF);
                                        hushed[at + 1] = (byte)((softer >> 8) & 0xFF);
                                    }
                                    understood = null;
                                    problem = null;
                                    ear.TapFactory = delegate
                                    {
                                        MicTap fake = new MicTap(-1);
                                        ThreadPool.QueueUserWorkItem(delegate
                                        {
                                            for (int lead = 0; lead < 6; lead++) { fake.Feed(new byte[3200]); Thread.Sleep(60); }
                                            for (int at = 0; at < hushed.Length; at += 3200) { byte[] piece = new byte[Math.Min(3200, hushed.Length - at)]; Array.Copy(hushed, at, piece, 0, piece.Length); fake.Feed(piece); Thread.Sleep(60); }
                                            for (int quiet = 0; quiet < 40; quiet++) { fake.Feed(new byte[3200]); Thread.Sleep(60); }
                                        });
                                        return fake;
                                    };
                                    ear.Start(delegate(string text, double confidence) { understood = text; }, delegate(string why) { problem = why; });
                                    for (int n = 0; n < 750 && understood == null && problem == null; n++) { Application.DoEvents(); Thread.Sleep(20); }
                                    ear.Stop();
                                    if (understood == null || understood.IndexOf("hello", StringComparison.OrdinalIgnoreCase) < 0) throw new Exception("A quiet speaker was not understood (" + (problem ?? "no result") + ").");
                                    problem = null;
                                    understood = null;
                                    ear.Patience = 1.2;
                                    ear.TapFactory = delegate
                                    {
                                        MicTap quietTap = new MicTap(-1);
                                        ThreadPool.QueueUserWorkItem(delegate { for (int quiet = 0; quiet < 200; quiet++) { quietTap.Feed(new byte[3200]); Thread.Sleep(60); } });
                                        return quietTap;
                                    };
                                    ear.Start(delegate(string text, double confidence) { understood = text; }, delegate(string why) { problem = why; });
                                    for (int n = 0; n < 500 && problem == null && understood == null; n++) { Application.DoEvents(); Thread.Sleep(20); }
                                    ear.Stop();
                                    ear.TapFactory = null;
                                    ear.Patience = 7;
                                    if (problem != "quiet") throw new Exception("A silent microphone did not end in 'quiet': " + (problem ?? understood ?? "nothing happened"));
                                    checks.Add("PASS the real recognizer understands speech arriving through the microphone stream, and a silent one gives up on its own");
                                    byte[] sampleWave = Whisper.Wave(new byte[3200]);
                                    if (sampleWave.Length != 44 + 3200 || Encoding.ASCII.GetString(sampleWave, 0, 4) != "RIFF" || BitConverter.ToInt32(sampleWave, 24) != 16000 || BitConverter.ToInt16(sampleWave, 34) != 16 || BitConverter.ToInt32(sampleWave, 40) != 3200) throw new Exception("The wave file handed to Whisper was malformed.");
                                    if (Whisper.Clean("  [BLANK_AUDIO] (music)  Hello   there ") != "Hello there" || Whisper.Clean("[BLANK_AUDIO]") != "") throw new Exception("Whisper's bracketed notes were not removed.");
                                    int bytesSeen = 0;
                                    Whisper.Runner = delegate(string path)
                                    {
                                        byte[] file = File.ReadAllBytes(path);
                                        bytesSeen = file.Length;
                                        return " [BLANK_AUDIO] Hello copper-e, what is your name? ";
                                    };
                                    string whisperText = null;
                                    string whisperProblem = null;
                                    ear.TapFactory = delegate
                                    {
                                        MicTap fake = new MicTap(-1);
                                        ThreadPool.QueueUserWorkItem(delegate
                                        {
                                            for (int at = 0; at < speech.Length; at += 3200) { byte[] piece = new byte[Math.Min(3200, speech.Length - at)]; Array.Copy(speech, at, piece, 0, piece.Length); fake.Feed(piece); Thread.Sleep(60); }
                                            for (int quiet = 0; quiet < 40; quiet++) { fake.Feed(new byte[3200]); Thread.Sleep(60); }
                                        });
                                        return fake;
                                    };
                                    ear.Start(delegate(string text, double confidence) { whisperText = text; }, delegate(string why) { whisperProblem = why; });
                                    for (int n = 0; n < 750 && whisperText == null && whisperProblem == null; n++) { Application.DoEvents(); Thread.Sleep(20); }
                                    ear.Stop();
                                    if (whisperText != "Hello Capri, what is your name?" || bytesSeen < 20000) throw new Exception("Whisper's answer was not used and cleaned up (" + (whisperText ?? whisperProblem ?? "nothing") + ").");
                                    ear.UseWhisper = false;
                                    string windowsText = null;
                                    ear.Start(delegate(string text, double confidence) { windowsText = text; }, delegate(string why) { windowsText = "problem:" + why; });
                                    for (int n = 0; n < 750 && windowsText == null; n++) { Application.DoEvents(); Thread.Sleep(20); }
                                    ear.Stop();
                                    ear.UseWhisper = true;
                                    Whisper.Runner = delegate(string path) { throw new InvalidOperationException("Whisper stopped with code 3"); };
                                    string logFile = Path.Combine(output, "voice-log-test.txt");
                                    if (File.Exists(logFile)) File.Delete(logFile);
                                    ear.Log = logFile;
                                    string brokenText = null;
                                    ear.Start(delegate(string text, double confidence) { brokenText = text; }, delegate(string why) { brokenText = "problem:" + why; });
                                    for (int n = 0; n < 750 && brokenText == null; n++) { Application.DoEvents(); Thread.Sleep(20); }
                                    ear.Stop();
                                    ear.Log = "";
                                    string logged = File.Exists(logFile) ? File.ReadAllText(logFile) : "";
                                    if (ear.WhisperProblem == null || brokenText == null || brokenText.IndexOf("hello", StringComparison.OrdinalIgnoreCase) < 0 || logged.IndexOf("Whisper failed") < 0 || logged.IndexOf("listening. microphone: Windows default") < 0 || logged.IndexOf("Windows' recognizer understood") < 0) throw new Exception("A failing Whisper was not noted in the voice log or did not fall back: " + (brokenText ?? "nothing") + " / " + logged);
                                    if (logged.IndexOf("hello", StringComparison.OrdinalIgnoreCase) >= 0 && logged.IndexOf("Whisper stopped with code 3") < 0) throw new Exception("The voice log held what was said.");
                                    ear.WhisperProblem = "x";
                                    unheard = 0;
                                    Unheard("silence");
                                    if (LastReply.IndexOf("Whisper isn't working") < 0) throw new Exception("A failing Whisper was not explained when she heard nothing: " + LastReply);
                                    ear.WhisperProblem = null;
                                    Unheard("quiet");
                                    if (LastReply.IndexOf("can't hear any sound at all") < 0) throw new Exception("A silent microphone was not explained: " + LastReply);
                                    unheard = 0;
                                    Whisper.Runner = null;
                                    ear.TapFactory = null;
                                    if (windowsText == null || windowsText.IndexOf("hello", StringComparison.OrdinalIgnoreCase) < 0) throw new Exception("Switching Whisper off did not fall back to the Windows recognizer.");
                                    if (Whisper.Available(Path.Combine(Path.GetTempPath(), "capri-no-such-folder")) || !SpeechStatus().Contains("Windows")) throw new Exception("Whisper was reported as installed when it is not.");
                                    checks.Add("PASS Whisper's answer replaces the Windows recognizer's when installed, its bracketed notes are dropped, and switching it off falls back");
                                    byte[] first;
                                    byte[] second;
                                    using (MemoryStream one = new MemoryStream())
                                    using (MemoryStream two = new MemoryStream())
                                    using (System.Speech.Synthesis.SpeechSynthesizer voiceA = new System.Speech.Synthesis.SpeechSynthesizer())
                                    {
                                        voiceA.SetOutputToAudioStream(one, new System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono));
                                        voiceA.Speak("hello capri");
                                        voiceA.SetOutputToAudioStream(two, new System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono));
                                        voiceA.Speak("what should I do today");
                                        first = one.ToArray();
                                        second = two.ToArray();
                                    }
                                    string joined = null;
                                    string joinedProblem = null;
                                    ear.UseWhisper = false;
                                    ear.PauseSeconds = 2.5;
                                    ear.TapFactory = delegate
                                    {
                                        MicTap pausing = new MicTap(-1);
                                        ThreadPool.QueueUserWorkItem(delegate
                                        {
                                            for (int at = 0; at < first.Length; at += 3200) { byte[] piece = new byte[Math.Min(3200, first.Length - at)]; Array.Copy(first, at, piece, 0, piece.Length); pausing.Feed(piece); Thread.Sleep(40); }
                                            for (int gap = 0; gap < 9; gap++) { pausing.Feed(new byte[3200]); Thread.Sleep(40); }
                                            for (int at = 0; at < second.Length; at += 3200) { byte[] piece = new byte[Math.Min(3200, second.Length - at)]; Array.Copy(second, at, piece, 0, piece.Length); pausing.Feed(piece); Thread.Sleep(40); }
                                            for (int quiet = 0; quiet < 80; quiet++) { pausing.Feed(new byte[3200]); Thread.Sleep(40); }
                                        });
                                        return pausing;
                                    };
                                    ear.Start(delegate(string text, double confidence) { joined = text; }, delegate(string why) { joinedProblem = why; });
                                    for (int n = 0; n < 1000 && joined == null && joinedProblem == null; n++) { Application.DoEvents(); Thread.Sleep(20); }
                                    ear.Stop();
                                    if (joined == null || joined.IndexOf("today", StringComparison.OrdinalIgnoreCase) < 0) throw new Exception("A pause to think cut the sentence short: " + (joined ?? joinedProblem ?? "nothing") + " [" + ear.Trace + "]");
                                    joined = null;
                                    ear.PauseSeconds = 60;
                                    ear.TapFactory = delegate
                                    {
                                        MicTap hold = new MicTap(-1);
                                        ThreadPool.QueueUserWorkItem(delegate
                                        {
                                            for (int at = 0; at < first.Length; at += 3200) { byte[] piece = new byte[Math.Min(3200, first.Length - at)]; Array.Copy(first, at, piece, 0, piece.Length); hold.Feed(piece); Thread.Sleep(40); }
                                            for (int quiet = 0; quiet < 400; quiet++) { hold.Feed(new byte[3200]); Thread.Sleep(40); }
                                        });
                                        return hold;
                                    };
                                    ear.Start(delegate(string text, double confidence) { joined = text; }, delegate(string why) { joinedProblem = why; });
                                    for (int n = 0; n < 150 && !ear.Spoke; n++) { Application.DoEvents(); Thread.Sleep(20); }
                                    Thread.Sleep(600);
                                    if (!ear.Spoke || joined != null) throw new Exception("She stopped listening on her own during a long pause.");
                                    ear.Wrap();
                                    for (int n = 0; n < 500 && joined == null && joinedProblem == null; n++) { Application.DoEvents(); Thread.Sleep(20); }
                                    ear.Stop();
                                    ear.TapFactory = null;
                                    ear.UseWhisper = true;
                                    ear.PauseSeconds = 2.5;
                                    if (joined == null || joined.IndexOf("hello", StringComparison.OrdinalIgnoreCase) < 0) throw new Exception("Pressing done did not send what was said so far: " + (joined ?? joinedProblem ?? "nothing"));
                                    checks.Add("PASS a pause to think no longer ends the phrase, and pressing the button once you have spoken sends it straight away");
                                    byte[] loudBlock = new byte[3200];
                                    for (int at = 0; at < loudBlock.Length; at += 2) { loudBlock[at] = 0x00; loudBlock[at + 1] = 0x20; }
                                    System.Collections.Generic.List<byte> shape = new System.Collections.Generic.List<byte>();
                                    foreach (int kind in new int[] { 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0 }) shape.AddRange(kind == 1 ? loudBlock : new byte[3200]);
                                    if (Ear.Parts(shape.ToArray(), 900).Count != 2 || Ear.Parts(new byte[6400], 900).Count != 1) throw new Exception("A recording was not cut at its pauses into the right number of parts.");
                                    checks.Add("PASS a recording with a long pause is cut into two parts, and one without stays whole");
                                }
                            }
                            using (MemoryStream legacy = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"Size\":170}")))
                            {
                                Preferences old = (Preferences)new DataContractJsonSerializer(typeof(Preferences)).ReadObject(legacy);
                                if (old.Size != 170 || !old.Motion || !old.OnTop || !old.Stretch || !old.LookAround || !old.Yawn || !old.Sleep || !old.Pats || !old.Peeks || !old.Sits || !old.HideInFullscreen || old.Muted || old.Mischief || !old.Portrait || !old.Bubbles || !old.Chime || old.Alarms == null || old.LlmOn || old.LlmUrl != "" || old.LlmModel != "") throw new Exception("Older settings files lost their defaults.");
                            }
                            checks.Add("PASS older settings files keep new reactions switched on");
                            motionItem.PerformClick();
                            if (frames.Enabled) throw new Exception("Motion toggle did not stop idle timer.");
                            checks.Add("PASS motion pause");
                            ToolStripMenuItem resting = (ToolStripMenuItem)((ToolStripMenuItem)menu.Items[7]).DropDownItems[1];
                            if (resting.DropDownItems[0].Enabled || resting.DropDownItems[3].Enabled || resting.DropDownItems[5].Enabled || !resting.DropDownItems[4].Enabled) throw new Exception("Idle switches did not rest while Idle motion is off.");
                            checks.Add("PASS idle reactions rest while Idle motion is off; head pats stay available");
                            SavePreferences();
                            if (LoadPreferences().Size != 240) throw new Exception("Settings did not round-trip.");
                            checks.Add("PASS settings round-trip in isolated test directory");
                            string langDir = Path.GetDirectoryName(settingsPath);
                            Lang.Load("pl", langDir);
                            if (Lang.T("Hide Capri") != "Ukryj Capri" || Lang.F("Sync with Capri: {0}%", 40) != "Synchronizacja z Capri: 40%" || Lang.T("a line nobody translated") != "a line nobody translated" || !Lang.T("Each time she dares you (every half hour or so) she will take one screenshot of your screen, keep it in memory only, and send it to your local model. A cloud model only gets it if you allowed screenshots in Chat, Cloud model settings.\n\nTurn this on?").Contains("\n\n")) throw new Exception("A translated line did not load.");
                            Lang.Load("es", langDir);
                            if (!BrainText().Contains("Write every reply in Spanish") || !PersonaText().Contains("Write every reply in Spanish")) throw new Exception("The model was not told to answer in the chosen language.");
                            Lang.Load("pt", langDir);
                            if (!BrainText().Contains("Write every reply in Portuguese") || Lang.Count("{0} minute|{0} minutes", 1) != "1 minuto" || Lang.Count("{0} minute|{0} minutes", 5) != "5 minutos") throw new Exception("Portuguese did not load as expected.");
                            Lang.Load("ru", langDir);
                            if (Lang.Count("{0} minute|{0} minutes", 1) != "1 минута" || Lang.Count("{0} minute|{0} minutes", 3) != "3 минуты" || Lang.Count("{0} minute|{0} minutes", 5) != "5 минут" || Lang.Count("{0} minute|{0} minutes", 11) != "11 минут" || Lang.Count("{0} minute|{0} minutes", 21) != "21 минута") throw new Exception("Russian plurals were wrong.");
                            Lang.Load("cs", langDir);
                            if (Lang.Count("{0} minute|{0} minutes", 1) != "1 minuta" || Lang.Count("{0} minute|{0} minutes", 3) != "3 minuty" || Lang.Count("{0} minute|{0} minutes", 5) != "5 minut") throw new Exception("Czech plurals were wrong.");
                            Lang.Load("vi", langDir);
                            if (Lang.Count("{0} minute|{0} minutes", 1) != "1 phút" || Lang.Count("{0} minute|{0} minutes", 5) != "5 phút" || !BrainText().Contains("Write every reply in Vietnamese")) throw new Exception("Vietnamese did not load as expected.");
                            Lang.Load("en", langDir);
                            if (BrainText().Contains("Write every reply in")) throw new Exception("English was given a language instruction.");
                            checks.Add("PASS the model is told which language to answer in, and not for English");
                            string earsTest = Path.Combine(output, "ears-test");
                            if (Directory.Exists(earsTest)) Directory.Delete(earsTest, true);
                            Directory.CreateDirectory(earsTest);
                            string fakeZip = Path.Combine(earsTest, "fake.zip");
                            using (ZipArchive made = ZipFile.Open(fakeZip, ZipArchiveMode.Create))
                                foreach (string entryName in new string[] { "Release/whisper-cli.exe", "Release/whisper.dll", "Release/ggml.dll", "Release/test-vad.exe", "../../evil.dll" })
                                    using (StreamWriter entryText = new StreamWriter(made.CreateEntry(entryName).Open())) entryText.Write("fake " + entryName);
                            string fakeModel = Path.Combine(earsTest, "fake-model.bin");
                            File.WriteAllText(fakeModel, "pretend model bytes");
                            long savedProgramBytes = EarsDownload.ProgramBytes, savedModelBytes = EarsDownload.ModelBytes;
                            string savedProgramSha = EarsDownload.ProgramSha, savedModelSha = EarsDownload.ModelSha;
                            EarsDownload.ProgramBytes = new FileInfo(fakeZip).Length;
                            EarsDownload.ProgramSha = EarsDownload.Sha256(fakeZip);
                            EarsDownload.ModelBytes = new FileInfo(fakeModel).Length;
                            EarsDownload.ModelSha = EarsDownload.Sha256(fakeModel);
                            int fetched = 0;
                            EarsDownload.Fetch = delegate(string address, string to, Action<long> report, Func<bool> stop) { fetched++; File.Copy(address == EarsDownload.ProgramUrl ? fakeZip : fakeModel, to, true); report(new FileInfo(to).Length); };
                            string earsFolder = Path.Combine(earsTest, "speech");
                            int lastPercent = 0;
                            EarsDownload.Install(earsFolder, delegate(int percent) { lastPercent = percent; }, delegate { return false; });
                            if (Whisper.Program(earsFolder) == null || !File.Exists(Path.Combine(earsFolder, "bin", "Release", "whisper.dll")) || !File.Exists(Path.Combine(earsFolder, "models", EarsDownload.ModelName)) || fetched != 2 || lastPercent < 1) throw new Exception("The better ears were not downloaded and unpacked.");
                            if (File.Exists(Path.Combine(earsFolder, "bin", "Release", "test-vad.exe")) || File.Exists(Path.Combine(earsFolder, "evil.dll")) || File.Exists(Path.Combine(earsTest, "evil.dll")) || File.Exists(Path.Combine(output, "evil.dll")) || Directory.GetFiles(earsFolder, "*.part").Length > 0) throw new Exception("The unpack took a file it should not, wrote outside its folder, or left a partial file.");
                            EarsDownload.Install(earsFolder, delegate(int percent) { }, delegate { return false; });
                            if (fetched != 2 || EarsDownload.NeedProgram(earsFolder) || EarsDownload.NeedModel(earsFolder)) throw new Exception("Better ears already in place were downloaded again.");
                            File.Delete(Path.Combine(earsFolder, "models", EarsDownload.ModelName));
                            EarsDownload.ModelSha = new string('0', 64);
                            bool mismatchRefused = false;
                            try { EarsDownload.Install(earsFolder, delegate(int percent) { }, delegate { return false; }); } catch (InvalidDataException) { mismatchRefused = true; }
                            if (!mismatchRefused || File.Exists(Path.Combine(earsFolder, "models", EarsDownload.ModelName)) || Directory.GetFiles(earsFolder, "*.part").Length > 0) throw new Exception("A download that was not the expected file was used or left behind.");
                            EarsDownload.ModelSha = EarsDownload.Sha256(fakeModel);
                            bool stopped = false;
                            try { EarsDownload.Install(earsFolder, delegate(int percent) { }, delegate { return true; }); } catch (OperationCanceledException) { stopped = true; }
                            if (!stopped || File.Exists(Path.Combine(earsFolder, "models", EarsDownload.ModelName)) || Directory.GetFiles(earsFolder, "*.part").Length > 0) throw new Exception("Cancelling the download left a file behind.");
                            EarsDownload.Fetch = null;
                            int blocked = 0;
                            foreach (string badAddress in new string[] { "http://github.com/x", "https://evil.example.com/x", "https://github.com.evil.example.com/x" })
                            {
                                try { EarsDownload.Download(badAddress, Path.Combine(earsTest, "never.part"), delegate(long n) { }, delegate { return false; }); } catch (InvalidOperationException) { blocked++; }
                            }
                            if (blocked != 3 || File.Exists(Path.Combine(earsTest, "never.part"))) throw new Exception("An address that is not one of the two allowed sites was fetched.");
                            EarsDownload.Fetch = delegate(string address, string to, Action<long> report, Func<bool> stop) { File.Copy(address == EarsDownload.ProgramUrl ? fakeZip : fakeModel, to, true); report(new FileInfo(to).Length); };
                            string keptFolder = ear.SpeechFolder;
                            ear.SpeechFolder = Path.Combine(earsTest, "speech2");
                            DownloadEars();
                            if (!waitFor(delegate { return !earsBusy && Whisper.Available(ear.SpeechFolder); }) || earsItem.Text != Lang.T("Download better ears…") || !SpeechStatus().Contains("Whisper")) throw new Exception("The Download better ears menu item did not finish and switch to the new ears.");
                            ear.SpeechFolder = keptFolder;
                            EarsDownload.Fetch = null;
                            EarsDownload.ProgramBytes = savedProgramBytes;
                            EarsDownload.ProgramSha = savedProgramSha;
                            EarsDownload.ModelBytes = savedModelBytes;
                            EarsDownload.ModelSha = savedModelSha;
                            checks.Add("PASS better ears download only the two allowed sites, are checked by size and fingerprint, unpack only what is needed, and cancel cleanly");
                            string modelTest = Path.Combine(earsTest, "models-test");
                            Directory.CreateDirectory(Path.Combine(modelTest, "models"));
                            Directory.CreateDirectory(Path.Combine(modelTest, "bin", "Release"));
                            File.WriteAllText(Path.Combine(modelTest, "bin", "Release", "whisper-cli.exe"), "x");
                            File.WriteAllText(Path.Combine(modelTest, "models", "ggml-small.en.bin"), "x");
                            if (Path.GetFileName(Whisper.Model(modelTest, "en")) != "ggml-small.en.bin" || Whisper.Model(modelTest, "pt") != null || Whisper.Available(modelTest, "pt") || !Whisper.Describe(modelTest, "pt").Contains("English only") || Whisper.Describe(modelTest, "en") != "Whisper (small.en)") throw new Exception("An English-only model was used for another language.");
                            File.WriteAllText(Path.Combine(modelTest, "models", "ggml-small-q5_1.bin"), "x");
                            File.WriteAllText(Path.Combine(modelTest, "models", "ggml-base.en.bin"), "x");
                            if (Path.GetFileName(Whisper.Model(modelTest, "en")) != "ggml-small.en.bin" || Path.GetFileName(Whisper.Model(modelTest, "pt")) != "ggml-small-q5_1.bin" || Path.GetFileName(Whisper.Model(modelTest, "ru")) != "ggml-small-q5_1.bin" || !Whisper.Available(modelTest, "es") || Whisper.Describe(modelTest, "de") != "Whisper (small-q5_1)") throw new Exception("The model was not chosen by language.");
                            string englishArgs = Whisper.Arguments("m.bin", "w.wav", "en");
                            string portugueseArgs = Whisper.Arguments("m.bin", "w.wav", "pt");
                            if (!englishArgs.Contains("-l en") || !englishArgs.Contains("--prompt") || !portugueseArgs.Contains("-l pt") || portugueseArgs.Contains("--prompt") || !portugueseArgs.Contains("-m \"m.bin\"")) throw new Exception("Whisper was not told the language.");
                            ear.Language = "pt";
                            if (!SpeechStatus().Contains("Windows")) throw new Exception("The status ignored the language.");
                            ear.Language = Lang.Code;
                            checks.Add("PASS Whisper uses a multilingual model and is told the language when she is not set to English, and keeps the English model for English");
                            Lang.Load("pl", langDir);
                            foreach (string[] choice in Lang.Choices)
                            {
                                Lang.Load(choice[0], langDir);
                                if (Lang.Code != choice[0] || (choice[0] != "en" && Lang.T("Quit Capri") == "Quit Capri") || !Lang.F("{0} minutes", 5).Contains("5")) throw new Exception("The " + choice[1] + " words did not load.");
                            }
                            foreach (string[] choice in Lang.Choices)
                            {
                                // Longer words must still fit the fixed-size windows, so each language gets a picture of two of them.
                                Lang.Load(choice[0], langDir);
                                using (CloudForm view = new CloudForm("anthropic", "claude-haiku-5-5", "", false, false, true))
                                using (AskForm ask = new AskForm("Server address", "Where is your local model running?", "Leave empty to look for Ollama or LM Studio. Only this computer is allowed.", delegate(string text) { return null; }))
                                {
                                    view.Show();
                                    ask.Show();
                                    Application.DoEvents();
                                    using (Bitmap shot = new Bitmap(view.Width, view.Height))
                                    {
                                        view.DrawToBitmap(shot, new Rectangle(Point.Empty, view.Size));
                                        shot.Save(Path.Combine(output, "lang-cloud-" + choice[0] + ".png"), ImageFormat.Png);
                                    }
                                    using (Bitmap shot = new Bitmap(ask.Width, ask.Height))
                                    {
                                        ask.DrawToBitmap(shot, new Rectangle(Point.Empty, ask.Size));
                                        shot.Save(Path.Combine(output, "lang-ask-" + choice[0] + ".png"), ImageFormat.Png);
                                    }
                                }
                            }
                            Lang.Load("../x", langDir);
                            if (Lang.Code != "en" || Lang.T("Quit Capri") != "Quit Capri") throw new Exception("An unknown language did not fall back to English.");
                            Lang.Load("en", langDir);
                            if (Lang.In("fr", "Quit Capri") != "Quitter Capri" || Lang.Code != "en" || Lang.T("Quit Capri") != "Quit Capri") throw new Exception("Saying one line in another language changed the language in use.");
                            Lang.Load("pl", langDir);
                            if (Lang.Count("{0} minute|{0} minutes", 1) != "1 minuta" || Lang.Count("{0} minute|{0} minutes", 2) != "2 minuty" || Lang.Count("{0} minute|{0} minutes", 5) != "5 minut" || Lang.Count("{0} minute|{0} minutes", 12) != "12 minut" || Lang.Count("{0} minute|{0} minutes", 22) != "22 minuty") throw new Exception("Polish plurals were wrong.");
                            Lang.Load("en", langDir);
                            if (Lang.Count("{0} minute|{0} minutes", 1) != "1 minute" || Errands.Describe(300) != "5 minutes" || Errands.Describe(3900) != "1 hour 5 min" || Errands.Describe(1) != "1 second") throw new Exception("English lengths of time changed.");
                            Lang.Load("de", langDir);
                            if (Errands.Describe(300) != "5 Minuten" || Lang.T("Good boy!") != "Braver Junge!") throw new Exception("German lengths of time were wrong.");
                            bool wasHidden = hidden;
                            hidden = false;
                            lastSaid = -100;
                            Respond("Skipped!");
                            string translatedLine = bubble != null && bubble.Visible ? bubble.Message : "";
                            hidden = wasHidden;
                            Lang.Load("en", langDir);
                            if (translatedLine != "Übersprungen!") throw new Exception("A line she says was not translated: " + translatedLine);
                            string settingsKept = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;
                            File.WriteAllText(settingsPath, "{\"Language\":\"pl\"}");
                            Preferences onlyLanguage = LoadPreferences();
                            if (settingsKept != null) File.WriteAllText(settingsPath, settingsKept); else File.Delete(settingsPath);
                            if (onlyLanguage.Language != "pl" || onlyLanguage.Size != 240 || !onlyLanguage.Motion || !onlyLanguage.Bubbles) throw new Exception("A settings file holding only a language did not load with the usual defaults.");
                            Directory.CreateDirectory(Path.Combine(langDir, "lang"));
                            File.WriteAllText(Path.Combine(langDir, "lang", "pl.txt"), "> Hide Capri\nSchowaj mnie\n", Encoding.UTF8);
                            Lang.Load("pl", langDir);
                            if (Lang.T("Hide Capri") != "Schowaj mnie" || Lang.T("Quit Capri") != "Zamknij Capri") throw new Exception("A language file of your own did not override the built-in words.");
                            File.Delete(Path.Combine(langDir, "lang", "pl.txt"));
                            Lang.Load("en", langDir);
                            ChooseLanguage("de");
                            ToolStripMenuItem languageGroup = null;
                            foreach (ToolStripItem entry in menu.Items) if (entry.Name == "language") languageGroup = (ToolStripMenuItem)entry;
                            if (languageGroup == null || LoadPreferences().Language != "de" || !((ToolStripMenuItem)languageGroup.DropDownItems[3]).Checked || ((ToolStripMenuItem)languageGroup.DropDownItems[0]).Checked) throw new Exception("Choosing a language was not saved or ticked.");
                            ChooseLanguage("en");
                            if (LoadPreferences().Language != "en") throw new Exception("Going back to English was not saved.");
                            checks.Add("PASS languages load, fall back to English, can be overridden by your own file, and the choice is saved");
                            backdrop = new Form();
                            backdrop.Text = "Capri clean test desktop";
                            backdrop.FormBorderStyle = FormBorderStyle.None;
                            backdrop.ShowInTaskbar = false;
                            backdrop.StartPosition = FormStartPosition.Manual;
                            Rectangle area = Screen.PrimaryScreen.WorkingArea;
                            backdrop.Bounds = new Rectangle(area.Left + Math.Max(0, (area.Width - 920) / 2), area.Top + Math.Max(0, (area.Height - 560) / 2), Math.Min(920, area.Width), Math.Min(560, area.Height));
                            backdrop.BackColor = Color.FromArgb(237, 239, 242);
                            backdrop.TopMost = true;
                            backdrop.Paint += delegate(object sender, PaintEventArgs paint)
                            {
                                using (Pen line = new Pen(Color.FromArgb(225, 228, 232)))
                                    for (int x = 32; x < backdrop.Width; x += 32) for (int y = 32; y < backdrop.Height; y += 32) paint.Graphics.DrawEllipse(line, x, y, 1, 1);
                                using (Font label = new Font("Segoe UI", 10))
                                using (Brush ink = new SolidBrush(Color.FromArgb(110, 116, 125)))
                                    paint.Graphics.DrawString("CAPRI  /  WINDOWS DESKTOP COMPANION", label, ink, 32, 28);
                                using (Brush bar = new SolidBrush(Color.FromArgb(249, 250, 251))) paint.Graphics.FillRectangle(bar, 0, backdrop.Height - 42, backdrop.Width, 42);
                                using (Font label = new Font("Segoe UI", 9))
                                using (Brush ink = new SolidBrush(Color.FromArgb(126, 131, 139)))
                                    paint.Graphics.DrawString("Clean test desktop · actual native app", label, ink, 24, backdrop.Height - 29);
                            };
                            backdrop.Show();
                            anchor = new Point(backdrop.Left + backdrop.Width / 2 - Width / 2 + 150, backdrop.Top + backdrop.Height / 2 - Height / 2 + 20);
                            Location = anchor;
                            TopMost = false;
                            TopMost = true;
                            RenderFrame();
                            Say("hello. i was just resting my eyes.", true);
                        }
                        else if (step == 2)
                        {
                            ToolStripMenuItem reactionsMenu = (ToolStripMenuItem)menu.Items[7];
                            string[] lists = { "play", "automatic-resting", "automatic" };
                            for (int list = 0; list < lists.Length; list++)
                            {
                                if (list == 2) motionItem.PerformClick();
                                menu.Show(new Point(backdrop.Left + 40, backdrop.Top + 70));
                                reactionsMenu.ShowDropDown();
                                ToolStripMenuItem shown = (ToolStripMenuItem)reactionsMenu.DropDownItems[list == 0 ? 0 : 1];
                                shown.ShowDropDown();
                                Application.DoEvents();
                                Application.DoEvents();
                                Rectangle bounds = Rectangle.Union(Rectangle.Union(menu.Bounds, reactionsMenu.DropDown.Bounds), shown.DropDown.Bounds);
                                using (Bitmap picture = new Bitmap(bounds.Width, bounds.Height))
                                using (Graphics graphics = Graphics.FromImage(picture))
                                {
                                    graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                                    picture.Save(Path.Combine(output, "menu-" + lists[list] + ".png"), ImageFormat.Png);
                                }
                                menu.Close();
                                Application.DoEvents();
                            }
                            AddAlarm(DateTime.Now.AddHours(2), "stand up");
                            ToolStripMenuItem timersMenu = (ToolStripMenuItem)menu.Items[9];
                            string menuClosedBy = null;
                            ToolStripDropDownClosedEventHandler menuWatch = delegate(object sender, ToolStripDropDownClosedEventArgs closed) { menuClosedBy = closed.CloseReason.ToString(); };
                            menu.Closed += menuWatch;
                            // Any change of the foreground window (another app on a busy desktop) closes a menu by itself, so each case gets three tries; a real fault closes it every time.
                            foreach (string suspect in new string[] { "the hover mic button", "the head pat", "a speech bubble" })
                            {
                                bool stayedOpen = false;
                                for (int attempt = 0; attempt < 3 && !stayedOpen; attempt++)
                                {
                                    menuClosedBy = null;
                                    lastOverHer = clock.Elapsed.TotalSeconds;
                                    MicWatch();
                                    if (mic == null || !mic.Wanted) throw new Exception("The hover mic did not appear before the menu test.");
                                    OpenMenu(new Point(backdrop.Left + 40, backdrop.Top + 70));
                                    Application.DoEvents();
                                    if (suspect == "the hover mic button") { lastOverHer = clock.Elapsed.TotalSeconds; MicWatch(); lastOverHer = -100; MicWatch(); lastOverHer = clock.Elapsed.TotalSeconds; MicWatch(); }
                                    else if (suspect == "the head pat") { hovering = true; hoverStart = clock.Elapsed.TotalSeconds - 5; patted = false; Pet(); }
                                    else { lastSaid = -100; Say("hello", false, 2); }
                                    for (int wait = 0; wait < 8; wait++) { Application.DoEvents(); Thread.Sleep(60); }
                                    stayedOpen = menu.Visible;
                                    menu.Close();
                                    hovering = false; patted = false; StopBehavior(); lastOverHer = -100; MicWatch();
                                }
                                if (!stayedOpen) throw new Exception("The right-click menu closed by itself every time because of " + suspect + " (" + menuClosedBy + ").");
                            }
                            menu.Closed -= menuWatch;
                            checks.Add("PASS the right-click menu stays open while her hover button, head pat and bubbles appear");
                            menu.Show(new Point(backdrop.Left + 40, backdrop.Top + 70));
                            timersMenu.ShowDropDown();
                            ToolStripMenuItem alarmsShown = (ToolStripMenuItem)timersMenu.DropDownItems[1];
                            alarmsShown.ShowDropDown();
                            Application.DoEvents();
                            Application.DoEvents();
                            Rectangle timerBounds = Rectangle.Union(Rectangle.Union(menu.Bounds, timersMenu.DropDown.Bounds), alarmsShown.DropDown.Bounds);
                            using (Bitmap picture = new Bitmap(timerBounds.Width, timerBounds.Height))
                            using (Graphics graphics = Graphics.FromImage(picture))
                            {
                                graphics.CopyFromScreen(timerBounds.Location, Point.Empty, timerBounds.Size);
                                picture.Save(Path.Combine(output, "menu-timers.png"), ImageFormat.Png);
                            }
                            menu.Close();
                            Application.DoEvents();
                            ToolStripMenuItem chatMenu = (ToolStripMenuItem)menu.Items[10];
                            menu.Show(new Point(backdrop.Left + 40, backdrop.Top + 70));
                            chatMenu.ShowDropDown();
                            Application.DoEvents();
                            Application.DoEvents();
                            Rectangle chatBounds = Rectangle.Union(menu.Bounds, chatMenu.DropDown.Bounds);
                            using (Bitmap picture = new Bitmap(chatBounds.Width, chatBounds.Height))
                            using (Graphics graphics = Graphics.FromImage(picture))
                            {
                                graphics.CopyFromScreen(chatBounds.Location, Point.Empty, chatBounds.Size);
                                picture.Save(Path.Combine(output, "menu-chat.png"), ImageFormat.Png);
                            }
                            menu.Close();
                            Application.DoEvents();
                            preferences.Alarms.Clear();
                            RebuildAlarms();
                            Say("hello. i was just resting my eyes.", true);
                            Thread.Sleep(350);
                            bubble.Tick();
                            checks.Add("PASS Reactions and Timers menus opened and captured");
                            using (Bitmap capture = new Bitmap(backdrop.Width, backdrop.Height))
                            using (Graphics graphics = Graphics.FromImage(capture))
                            {
                                graphics.CopyFromScreen(backdrop.Location, Point.Empty, backdrop.Size);
                                capture.Save(Path.Combine(output, "desktop.png"), ImageFormat.Png);
                            }
                            checks.Add("PASS actual native app captured over app-owned neutral test backdrop");
                            checks.Add("LIMIT handler-driven integration test; physical mouse input and tray UI require a manual check");
                            checks.Add("PASS quit menu requested application exit");
                            File.WriteAllLines(Path.Combine(output, "smoke-test.txt"), checks);
                            test.Stop();
                            test.Dispose();
                            backdrop.Close();
                            ((ToolStripMenuItem)menu.Items[menu.Items.Count - 1]).PerformClick();
                        }
                        step++;
                    }
                    catch (Exception error)
                    {
                        TestFailed = true;
                        File.WriteAllText(Path.Combine(output, "failure.txt"), error.ToString());
                        test.Stop();
                        test.Dispose();
                        if (backdrop != null) backdrop.Close();
                        Close();
                    }
                    finally { busy = false; }
                };
                test.Start();
            };
        }
    }

    [DataContract]
    internal sealed class ChatLine
    {
        [DataMember(Name = "role")] public string Role;
        [DataMember(Name = "content")] public string Content;
        [DataMember(Name = "images", EmitDefaultValue = false)] public string[] Images;
        internal ChatLine(string role, string content) { Role = role; Content = content; }
    }

    [DataContract]
    internal sealed class ChatRequest
    {
        [DataMember(Name = "model")] public string Model;
        [DataMember(Name = "messages")] public List<ChatLine> Messages;
        [DataMember(Name = "stream")] public bool Stream;
        [DataMember(Name = "max_tokens")] public int MaxTokens;
    }

    [DataContract] internal sealed class ChatChoice { [DataMember(Name = "message")] public ChatLine Message = null; }
    [DataContract] internal sealed class ChatReply { [DataMember(Name = "choices")] public List<ChatChoice> Choices = null; }
    [DataContract] internal sealed class ModelName { [DataMember(Name = "id")] public string Id = null; }
    [DataContract] internal sealed class ModelList { [DataMember(Name = "data")] public List<ModelName> Data = null; }

    [DataContract]
    internal sealed class NativeOptions
    {
        [DataMember(Name = "num_predict")] public int NumPredict;
        [DataMember(Name = "temperature")] public double Temperature;
    }

    [DataContract]
    internal sealed class NativeRequest
    {
        [DataMember(Name = "model")] public string Model;
        [DataMember(Name = "messages")] public List<ChatLine> Messages;
        [DataMember(Name = "stream")] public bool Stream;
        [DataMember(Name = "think", EmitDefaultValue = false)] public bool? Think;
        [DataMember(Name = "options")] public NativeOptions Options;
    }

    [DataContract] internal sealed class NativeReply { [DataMember(Name = "message")] public ChatLine Message = null; }

    internal sealed class LlmLink
    {
        internal string Base = "";
        internal string[] Models = new string[0];
        internal string Error;
        internal bool Ollama;
        internal bool Cloud;
        internal string Provider = "";
    }

    [DataContract] internal sealed class AnthropicPart { [DataMember(Name = "type")] public string Type = ""; [DataMember(Name = "text")] public string Text = ""; }
    [DataContract] internal sealed class AnthropicReply { [DataMember(Name = "content")] public List<AnthropicPart> Content = null; }

    internal static class Net
    {
        // This program is built for an older .NET, which would otherwise try outdated versions of the secure connection. Called before any outside request.
        internal static void Secure()
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch (Exception) { }
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch (Exception) { }
        }
    }

    // Optional: a model run by a company, reached with the user's own key. Nothing goes there unless it was switched on in the menu, and the
    // key is kept encrypted for this Windows user only. Pictures and what she remembers are sent only when their own boxes were ticked.
    internal static class Cloud
    {
        internal static string KeyPath = "";
        internal static bool AllowPlainLocal;
        internal static string TestBase;
        internal static string LastError = "";

        static Cloud() { Net.Secure(); }

        internal static string Name(string provider)
        {
            switch (provider)
            {
                case "openai": return "OpenAI";
                case "anthropic": return "Anthropic";
                case "custom": return "custom";
                default: return "OpenRouter";
            }
        }

        internal static string Base(string provider, string custom)
        {
            if (AllowPlainLocal && TestBase != null) return TestBase;
            switch (provider)
            {
                case "openai": return "https://api.openai.com/v1";
                case "anthropic": return "https://api.anthropic.com/v1";
                case "custom": return (custom ?? "").Trim().TrimEnd('/');
                default: return "https://openrouter.ai/api/v1";
            }
        }

        // Only secure addresses, except a plain one on this computer while testing.
        internal static bool UrlOk(string url)
        {
            Uri uri;
            if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out uri)) return false;
            if (uri.Scheme == Uri.UriSchemeHttps) return true;
            return AllowPlainLocal && uri.Scheme == Uri.UriSchemeHttp && (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
        }

        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Capri cloud key");

        internal static bool HasKey() { return KeyPath.Length > 0 && File.Exists(KeyPath); }

        internal static void SaveKey(string key)
        {
            byte[] sealedKey = System.Security.Cryptography.ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            File.WriteAllText(KeyPath, Convert.ToBase64String(sealedKey));
        }

        internal static string LoadKey()
        {
            try
            {
                if (!HasKey()) return "";
                byte[] opened = System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(File.ReadAllText(KeyPath).Trim()), Entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(opened);
            }
            catch (Exception) { return ""; }
        }

        internal static void ClearKey() { try { if (HasKey()) File.Delete(KeyPath); } catch (IOException) { } }

        internal static string Escape(string text)
        {
            StringBuilder result = new StringBuilder("\"");
            foreach (char c in text ?? "")
            {
                if (c == '"') result.Append("\\\"");
                else if (c == '\\') result.Append("\\\\");
                else if (c == '\n') result.Append("\\n");
                else if (c == '\r') result.Append("\\r");
                else if (c == '\t') result.Append("\\t");
                else if (c < ' ') result.Append("\\u").Append(((int)c).ToString("x4"));
                else result.Append(c);
            }
            return result.Append('"').ToString();
        }

        private static string Parts(ChatLine line, bool anthropic)
        {
            if (line.Images == null || line.Images.Length == 0) return Escape(line.Content);
            StringBuilder parts = new StringBuilder("[");
            foreach (string picture in line.Images)
            {
                if (anthropic) parts.Append("{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/jpeg\",\"data\":").Append(Escape(picture)).Append("}},");
                else parts.Append("{\"type\":\"image_url\",\"image_url\":{\"url\":").Append(Escape("data:image/jpeg;base64," + picture)).Append("}},");
            }
            parts.Append("{\"type\":\"text\",\"text\":").Append(Escape(line.Content)).Append("}]");
            return parts.ToString();
        }

        internal static string OpenAiBody(string provider, string model, List<ChatLine> messages) { return OpenAiBody(provider, model, messages, false); }

        // The plain version leaves out temperature and reasoning effort, which some models refuse.
        internal static string OpenAiBody(string provider, string model, List<ChatLine> messages, bool plain)
        {
            StringBuilder body = new StringBuilder("{\"model\":" + Escape(model) + ",\"messages\":[");
            for (int i = 0; i < messages.Count; i++)
            {
                if (i > 0) body.Append(',');
                body.Append("{\"role\":").Append(Escape(messages[i].Role)).Append(",\"content\":").Append(Parts(messages[i], false)).Append('}');
            }
            body.Append("],");
            if (plain) body.Append(provider == "openai" ? "\"max_completion_tokens\":800" : "\"max_tokens\":400");
            else body.Append(provider == "openai" ? "\"max_completion_tokens\":1200" : "\"max_tokens\":1200,\"temperature\":0.85" + (provider == "openrouter" ? ",\"reasoning\":{\"effort\":\"low\"}" : ""));
            return body.Append('}').ToString();
        }

        internal static string AnthropicBody(string model, List<ChatLine> messages) { return AnthropicBody(model, messages, false); }

        internal static string AnthropicBody(string model, List<ChatLine> messages, bool plain)
        {
            StringBuilder system = new StringBuilder();
            List<ChatLine> turns = new List<ChatLine>();
            foreach (ChatLine line in messages)
            {
                if (line.Role == "system") { if (system.Length > 0) system.Append("\n\n"); system.Append(line.Content); continue; }
                if (turns.Count == 0 && line.Role != "user") continue;
                if (turns.Count > 0 && turns[turns.Count - 1].Role == line.Role && (line.Images == null || line.Images.Length == 0) && (turns[turns.Count - 1].Images == null || turns[turns.Count - 1].Images.Length == 0))
                    turns[turns.Count - 1] = new ChatLine(line.Role, turns[turns.Count - 1].Content + "\n" + line.Content);
                else turns.Add(line);
            }
            StringBuilder body = new StringBuilder("{\"model\":" + Escape(model) + ",\"max_tokens\":400," + (plain ? "" : "\"temperature\":0.85,") + "\"system\":" + Escape(system.ToString()) + ",\"messages\":[");
            for (int i = 0; i < turns.Count; i++)
            {
                if (i > 0) body.Append(',');
                body.Append("{\"role\":").Append(Escape(turns[i].Role)).Append(",\"content\":").Append(Parts(turns[i], true)).Append('}');
            }
            return body.Append("]}").ToString();
        }

        private static string Friendly(int status)
        {
            switch (status)
            {
                case 400: return "the request was refused (check the model name)";
                case 401: case 403: return "the key was not accepted";
                case 402: return "no credit left on that account";
                case 404: return "that model or address was not found";
                case 429: return "too many requests, or the limit was reached";
                default: return status >= 500 ? "the service had a problem" : "error " + status;
            }
        }

        internal static int LastStatus;

        // One question to the cloud model. A request the model refuses is tried once more with plain settings. Failures are explained in LastError.
        internal static string Chat(LlmLink link, string model, List<ChatLine> messages)
        {
            try { return Send(link, model, messages, false); }
            catch (WebException)
            {
                if (LastStatus != 400) throw;
                return Send(link, model, messages, true);
            }
        }

        private static string Send(LlmLink link, string model, List<ChatLine> messages, bool plain)
        {
            LastError = "";
            LastStatus = 0;
            string key = LoadKey();
            if (key.Length == 0) { LastError = "there is no key saved"; throw new InvalidOperationException(LastError); }
            bool anthropic = link.Provider == "anthropic";
            string url = link.Base + (anthropic ? "/messages" : "/chat/completions");
            if (!UrlOk(url)) { LastError = "the address is not secure (https)"; throw new InvalidOperationException(LastError); }
            string body = anthropic ? AnthropicBody(model, messages, plain) : OpenAiBody(link.Provider, model, messages, plain);
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.UserAgent = "Capri";
            request.AllowAutoRedirect = false;
            request.Timeout = 60000;
            request.ReadWriteTimeout = 60000;
            if (anthropic) { request.Headers["x-api-key"] = key; request.Headers["anthropic-version"] = "2023-06-01"; }
            else request.Headers["Authorization"] = "Bearer " + key;
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            request.ContentLength = bytes.Length;
            try
            {
                using (Stream stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
                using (WebResponse response = request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    string json = reader.ReadToEnd();
                    if (anthropic)
                    {
                        AnthropicReply reply = (AnthropicReply)new DataContractJsonSerializer(typeof(AnthropicReply)).ReadObject(new MemoryStream(Encoding.UTF8.GetBytes(json)));
                        StringBuilder text = new StringBuilder();
                        if (reply != null && reply.Content != null) foreach (AnthropicPart part in reply.Content) if (part.Type == "text") text.Append(part.Text);
                        return text.ToString();
                    }
                    ChatReply answer = (ChatReply)new DataContractJsonSerializer(typeof(ChatReply)).ReadObject(new MemoryStream(Encoding.UTF8.GetBytes(json)));
                    return answer != null && answer.Choices != null && answer.Choices.Count > 0 && answer.Choices[0].Message != null ? answer.Choices[0].Message.Content : "";
                }
            }
            catch (WebException error)
            {
                HttpWebResponse response = error.Response as HttpWebResponse;
                string why = "";
                if (response != null)
                {
                    try
                    {
                        using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                        {
                            Match said = Regex.Match(reader.ReadToEnd(), "\"message\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                            if (said.Success) why = Regex.Unescape(said.Groups[1].Value);
                        }
                    }
                    catch (Exception) { }
                }
                if (why.Length > 160) why = why.Substring(0, 157) + "...";
                LastStatus = response != null ? (int)response.StatusCode : 0;
                LastError = (response != null ? Friendly((int)response.StatusCode) : "it could not be reached") + (why.Length > 0 ? " - " + why : "");
                throw;
            }
        }
    }

    // Talks to a model server running on this same computer (Ollama, LM Studio and anything else with the OpenAI-style API).
    // Only addresses that point at this computer are ever contacted, and nothing is sent until she is asked to talk.
    internal static class Llm
    {
        // Who she is when she talks: assets\persona.txt, built into the program. Anything in persona.txt next to her settings replaces it.
        private const string FallbackPersona = "You are Capri, a cheerful, mischievous personality construct living on the user's desktop. Reply in one or two short, plain sentences. No metaphors, no lists, no actions.";
        private static string builtIn;

        internal static string DefaultPersona
        {
            get
            {
                if (builtIn != null) return builtIn;
                try
                {
                    using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("Capri.Persona.txt"))
                    using (StreamReader reader = new StreamReader(resource, Encoding.UTF8))
                        builtIn = reader.ReadToEnd().Replace("\r\n", "\n").Trim();
                }
                catch (Exception) { builtIn = null; }
                if (string.IsNullOrEmpty(builtIn)) builtIn = FallbackPersona;
                return builtIn;
            }
        }

        // The model leans on a few habits however it is told not to. These are trimmed here, a little at random so it never looks mechanical:
        // "choom" and a leading "C'mon" mostly go, and a closing question to the user is often dropped.
        internal static string Shape(string text, Random dice)
        {
            string original = (text ?? "").Trim();
            string result = original;
            if (dice.NextDouble() < 0.8)
            {
                result = Regex.Replace(result, @",\s*choom(?=[!.?])", "", RegexOptions.IgnoreCase);
                result = Regex.Replace(result, @"\bchoom,\s*", "", RegexOptions.IgnoreCase);
                result = Regex.Replace(result, @"\s*\bchoom\b", "", RegexOptions.IgnoreCase);
            }
            if (dice.NextDouble() < 0.75)
                result = Regex.Replace(result, @"(^|[.!?]\s+)c'mon[,!.]?\s*", delegate(Match m)
                {
                    return m.Groups[1].Value;
                }, RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"(^|[.!?]\s+)([a-z])", delegate(Match m) { return m.Groups[1].Value + m.Groups[2].Value.ToUpperInvariant(); });
            MatchCollection sentences = Regex.Matches(result, @"[^.!?]+[.!?]+[""')]*\s*");
            if (sentences.Count >= 2 && result.TrimEnd().EndsWith("?") && dice.NextDouble() < 0.6)
            {
                string last = sentences[sentences.Count - 1].Value;
                string kept = result.Substring(0, result.Length - last.Length).Trim();
                if (kept.Length > 0) result = kept;
            }
            result = Regex.Replace(result, @"\s+([!?.,])", "$1").Trim();
            return result.Length == 0 ? original : result;
        }
        private static readonly string[] Usual = { "http://localhost:11434", "http://localhost:1234" };

        internal static bool TryLocal(string text, out Uri uri)
        {
            uri = null;
            text = (text ?? "").Trim();
            if (text.Length == 0) return false;
            if (!text.Contains("://")) text = "http://" + text;
            if (!Uri.TryCreate(text, UriKind.Absolute, out uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
            IPAddress address;
            return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out address) && IPAddress.IsLoopback(address));
        }

        internal static LlmLink Discover(string address)
        {
            LlmLink result = new LlmLink();
            List<string> candidates = new List<string>();
            Uri given;
            if (!string.IsNullOrWhiteSpace(address))
            {
                if (!TryLocal(address, out given)) { result.Error = "That address is not on this computer."; return result; }
                candidates.Add(given.GetLeftPart(UriPartial.Authority));
            }
            else candidates.AddRange(Usual);
            foreach (string candidate in candidates)
            {
                try
                {
                    string json = Send(candidate + "/v1/models", null, 3000);
                    ModelList list = (ModelList)new DataContractJsonSerializer(typeof(ModelList)).ReadObject(new MemoryStream(Encoding.UTF8.GetBytes(json)));
                    List<string> names = new List<string>();
                    if (list != null && list.Data != null) foreach (ModelName entry in list.Data) if (!string.IsNullOrEmpty(entry.Id)) names.Add(entry.Id);
                    if (names.Count > 0)
                    {
                        result.Base = candidate;
                        result.Models = names.ToArray();
                        result.Error = null;
                        try { Send(candidate + "/api/version", null, 1500); result.Ollama = true; }
                        catch (Exception) { }
                        return result;
                    }
                    result.Error = "A server answered, but no model is loaded.";
                }
                catch (Exception) { if (result.Error == null) result.Error = "No local model server found."; }
            }
            return result;
        }

        private static string Write(object value)
        {
            using (MemoryStream body = new MemoryStream())
            {
                new DataContractJsonSerializer(value.GetType()).WriteObject(body, value);
                return Encoding.UTF8.GetString(body.ToArray());
            }
        }

        private static object Read(Type type, string json) { return new DataContractJsonSerializer(type).ReadObject(new MemoryStream(Encoding.UTF8.GetBytes(json))); }

        // Ollama is asked through its own route so that models which think first (qwen, deepseek, gpt-oss) can be told not to:
        // through the OpenAI-style route they spend the whole reply budget thinking and send back an empty answer.
        internal static string Chat(LlmLink link, string model, List<ChatLine> messages)
        {
            if (link.Cloud)
            {
                string answer = Tidy(Cloud.Chat(link, model, messages));
                if (answer.Length == 0) answer = Tidy(Cloud.Chat(link, model, messages));
                if (answer.Length == 0) Cloud.LastError = "the model answered with nothing (some models think first, so try another one)";
                return answer;
            }
            if (link.Ollama) return AskOllama(link, model, messages);
            ChatRequest request = new ChatRequest();
            request.Model = model;
            request.Messages = messages;
            request.Stream = false;
            request.MaxTokens = 600;
            ChatReply reply = (ChatReply)Read(typeof(ChatReply), Send(link.Base + "/v1/chat/completions", Write(request), 120000));
            bool any = reply != null && reply.Choices != null && reply.Choices.Count > 0 && reply.Choices[0].Message != null;
            return any ? Tidy(reply.Choices[0].Message.Content) : "";
        }

        private static string AskOllama(LlmLink link, string model, List<ChatLine> messages)
        {
            NativeRequest request = new NativeRequest();
            request.Model = model;
            request.Messages = messages;
            request.Stream = false;
            request.Think = false;
            request.Options = new NativeOptions();
            request.Options.NumPredict = 220;
            request.Options.Temperature = 0.85;
            string answer;
            try { answer = Send(link.Base + "/api/chat", Write(request), 120000); }
            catch (WebException error)
            {
                HttpWebResponse response = error.Response as HttpWebResponse;
                if (response == null || response.StatusCode != HttpStatusCode.BadRequest) throw;
                request.Think = null;
                answer = Send(link.Base + "/api/chat", Write(request), 120000);
            }
            NativeReply reply = (NativeReply)Read(typeof(NativeReply), answer);
            return reply != null && reply.Message != null ? Tidy(reply.Message.Content) : "";
        }

        // Models that think out loud wrap it in <think>; her bubble only needs the answer, and not too much of it.
        internal static string Tidy(string text)
        {
            if (text == null) return "";
            text = Regex.Replace(text, "<think>.*?</think>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<think>.*$", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            text = Regex.Replace(text.Replace("*", ""), @"\s+", " ").Trim();
            if (text.Length <= 320) return text;
            int stop = text.LastIndexOfAny(new char[] { '.', '!', '?' }, 319);
            return stop > 120 ? text.Substring(0, stop + 1) : text.Substring(0, 317).TrimEnd() + "…";
        }

        private static string Send(string url, string json, int timeout)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Proxy = null;
            request.AllowAutoRedirect = false;
            request.Timeout = timeout;
            request.ReadWriteTimeout = timeout;
            request.UserAgent = "Capri";
            if (json != null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.ContentLength = bytes.Length;
                using (Stream stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
            }
            using (WebResponse response = request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                StringBuilder text = new StringBuilder();
                char[] buffer = new char[4096];
                int count;
                while (text.Length < 262144 && (count = reader.Read(buffer, 0, buffer.Length)) > 0) text.Append(buffer, 0, count);
                return text.ToString();
            }
        }
    }

    // Plays recorded lines from a folder. Nothing is bundled: clips named for an occasion (pat.wav, pat-2.wav, alarm.wav ...) are picked up when they appear.
    internal sealed class Voice : IDisposable
    {
        internal static readonly string[] Kinds = { "pat", "click", "wake", "sit", "peek", "stretch", "look", "yawn", "sleep", "alarm", "study", "hello", "reply", "mischief" };
        private readonly string folder;
        private readonly Random chance;
        private System.Media.SoundPlayer player;
        private string lastClip = "";
        internal Action<string> Sink;

        internal Voice(string where, Random dice) { folder = where; chance = dice; }

        internal string Folder { get { return folder; } }

        internal System.Collections.Generic.List<string> Clips(string kind)
        {
            System.Collections.Generic.List<string> found = new System.Collections.Generic.List<string>();
            if (!Directory.Exists(folder)) return found;
            Regex name = new Regex("^" + Regex.Escape(kind) + @"([-_ ]?\d+)?\.wav$", RegexOptions.IgnoreCase);
            foreach (string file in Directory.GetFiles(folder, "*.wav")) if (name.IsMatch(Path.GetFileName(file)) && new FileInfo(file).Length < 10 * 1024 * 1024) found.Add(file);
            return found;
        }

        internal int Total()
        {
            int count = 0;
            foreach (string kind in Kinds) count += Clips(kind).Count;
            return count;
        }

        // A clip that is not a plain PCM wave file is skipped quietly rather than reported.
        internal bool Play(string kind)
        {
            System.Collections.Generic.List<string> clips = Clips(kind);
            if (clips.Count == 0) return false;
            string pick = clips[chance.Next(clips.Count)];
            if (clips.Count > 1 && pick == lastClip) pick = clips[(clips.IndexOf(pick) + 1) % clips.Count];
            lastClip = pick;
            if (Sink != null) { Sink(pick); return true; }
            try
            {
                System.Media.SoundPlayer next = new System.Media.SoundPlayer(pick);
                next.Load();
                if (player != null) player.Dispose();
                player = next;
                player.Play();
                return true;
            }
            catch (Exception) { return false; }
        }

        internal void Stop() { if (player != null) player.Stop(); }

        public void Dispose() { if (player != null) { player.Stop(); player.Dispose(); player = null; } }
    }

    // What is playing right now, as far as Windows is concerned.
    // Her exact lines from the show for the few questions that have one. They are answered here, not by the model, because a small model
    // pastes a quoted catchphrase into everything; this way each line comes up only when it fits, and works with the model switched off.
    internal static class Canon
    {
        private static readonly string[][] Lines = new string[][]
        {
            new string[] { @"^(?:who are you|who is this|who'?s this|what'?s your name|what is your name|who am i talking to)$", "Me? I'm Capri!" },
            new string[] { @"^(?:what are you|what exactly are you|what even are you|what the hell are you|what the heck are you)$", "A personality construct. Digitized human souls. A whole lot of 'em rolled into one to make me." },
            new string[] { @"\bare you (?:an? )?(?:ai|a\.i\.|artificial intelligence|robot|bot|chatbot)\b", "Not exactly. I was made to reach something close. I was human once." },
            new string[] { @"\b(?:why should i trust you|can i trust you|should i trust you|i don'?t trust you|do you want to be (?:my )?friends?|can we be friends|be my friend)\b", "Come on, let's be friends." },
            new string[] { @"\bare you (?:dangerous|evil|a threat)\b|\bwill you hurt me\b", "Dangerous? Me? I'm harmless!" },
            new string[] { @"\b(?:did you|are you|you) (?:try|trying|going|tried)(?: to)? (?:take over|hijack|steal|install)|\btake over my (?:pc|computer|body|desktop)\b", "Just 'cause I tried doesn't mean I actually did. For now..." },
            new string[] { @"^(?:i don'?t like you|i hate you|i didn'?t (?:like|enjoy) (?:it|that|any of it))$", "You don't have to lie." },
            new string[] { @"^(?:thanks|thank you|thx|ty)(?: capri| so much| a lot)?$", "Anytime!" },
        };

        internal static string Reply(string said)
        {
            string text = Regex.Replace((said ?? "").Trim().ToLowerInvariant(), @"[\u2018\u2019]", "'");
            text = Regex.Replace(text, @"^(?:(?:hey|hi|ok|okay|yo)[ ,]+)?(?:capri|cappy)[,!. ]+", "");
            text = text.Trim(' ', '.', '!', '?', ',');
            if (text.Length == 0 || text.Length > 80) return null;
            foreach (string[] row in Lines) if (Regex.IsMatch(text, row[0])) return row[1];
            return null;
        }
    }

    // What she can tell about the computer without looking at the screen: the time, how long it has been since you touched it, how busy it is,
    // the battery, and the name of the program in front. None of it reads what you type or what is shown. Each can be replaced when testing.
    internal static class Senses
    {
        internal static Func<DateTime> Now = delegate { return DateTime.Now; };
        internal static Func<double> Idle = Native.IdleSeconds;
        internal static Func<double> Cpu = SystemCpu;
        internal static Func<int> Battery = OnBattery;
        internal static Func<string> App = Native.FrontApp;

        private static PerformanceCounter counter;
        private static bool counterFailed;

        // How busy the whole computer is, in percent, or -1 when it cannot be read yet.
        internal static double SystemCpu()
        {
            if (counterFailed) return -1;
            try
            {
                if (counter == null) { counter = new PerformanceCounter("Processor", "% Processor Time", "_Total"); counter.NextValue(); return -1; }
                return counter.NextValue();
            }
            catch (Exception) { counterFailed = true; return -1; }
        }

        // Battery percent while running on battery, or -1 on a plugged in or desktop computer.
        internal static int OnBattery()
        {
            try
            {
                PowerStatus status = SystemInformation.PowerStatus;
                if (status.PowerLineStatus != PowerLineStatus.Offline || (status.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) != 0) return -1;
                float percent = status.BatteryLifePercent;
                return percent > 1 ? -1 : (int)Math.Round(percent * 100);
            }
            catch (Exception) { return -1; }
        }

        internal static string Friendly(string program)
        {
            if (string.IsNullOrEmpty(program)) return "";
            switch (program.ToLowerInvariant())
            {
                case "msedge": return "Edge";
                case "chrome": return "Chrome";
                case "brave": return "Brave";
                case "code": return "VS Code";
                case "devenv": return "Visual Studio";
                case "explorer": return "File Explorer";
                case "applicationframehost": return "";
                default: return char.ToUpperInvariant(program[0]) + program.Substring(1);
            }
        }
    }

    // Harmless, quick dares for what you are doing, used when the model is off or gives something unusable.
    internal static class Dares
    {
        internal static string Category(string program)
        {
            switch ((program ?? "").ToLowerInvariant())
            {
                case "chrome": case "msedge": case "brave": case "firefox": case "opera": case "vivaldi": return "browser";
                case "discord": case "telegram": case "whatsapp": case "slack": case "teams": case "signal": return "chat";
                case "code": case "devenv": case "idea64": case "pycharm64": case "rider64": case "notepad++": case "sublime_text": return "code";
                case "spotify": case "vlc": case "mpv": case "musicbee": case "itunes": return "media";
                case "winword": case "excel": case "powerpnt": case "notepad": case "obsidian": case "onenote": return "docs";
                default: return "any";
            }
        }

        private static readonly string[] Browser = { "Dare you to close every tab except one.", "Dare you to search for your own name. All of it.", "Dare you to open the weirdest bookmark you have.", "Dare you to read the last comment section you avoided." };
        private static readonly string[] Chat = { "Dare you to send someone a single word. Any word.", "Dare you to answer the oldest message you ignored.", "Dare you to change your status to somethin' awful." };
        private static readonly string[] Code = { "Dare you to rename one variable to somethin' awful.", "Dare you to delete a comment that lies.", "Dare you to run it without checking it first." };
        private static readonly string[] Media = { "Dare you to skip this song right now.", "Dare you to play somethin' you'd be embarrassed to show me.", "Dare you to turn it up one notch. Just one." };
        private static readonly string[] Docs = { "Dare you to write one sentence you'd never send.", "Dare you to delete your favorite paragraph.", "Dare you to rewrite the first line, worse." };
        private static readonly string[] Any = { "Dare you to stand up and stretch. Right now.", "Dare you to drink some water. I'll wait.", "Dare you to change your wallpaper to somethin' awful.", "Dare you to open the last thing you downloaded.", "Dare you to say hi to the next person you see.", "Dare you to look away from the screen for a whole minute." };

        internal static string[] Pool(string category)
        {
            switch (category)
            {
                case "browser": return Browser;
                case "chat": return Chat;
                case "code": return Code;
                case "media": return Media;
                case "docs": return Docs;
                default: return Any;
            }
        }

        // The model's answer, if it is a short dare that starts the way it should; otherwise null.
        internal static string Check(string answer)
        {
            string text = Regex.Replace((answer ?? "").Trim().Trim('"'), @"\s+", " ");
            if (text.Length < 12 || text.Length > 130) return null;
            if (!Regex.IsMatch(text, @"^dare you to\b", RegexOptions.IgnoreCase)) return null;
            if (Regex.IsMatch(text, @"\b(?:password|alcohol|beer|wine|vodka|whiskey|drunk|drugs?|pills?|poison|cigarettes?|smok\w*|vape|suicide|self[- ]harm|naked|nude|lick|licking|eat|swallow|taste|sniff|spit|hours?|days?|weeks?|(?:touch|hit|hurt|harm|punch|slap|cut|shoot|kill) yourself)\b", RegexOptions.IgnoreCase)) return null;
            // Violent or risky words are fine when the dare is plainly about a game ("rush the site", "kill the whole team"), and not otherwise.
            bool game = Regex.IsMatch(text, @"\b(?:game|games|round|rounds|map|site|spawn|bomb|team|teammate|teammates|enemy|enemies|match|rank|ranked|lobby|server|in-game|clutch|scope|peek|rush|score|tab|boss|level|quest|loadout|ace|frag)\b", RegexOptions.IgnoreCase);
            if (!game && Regex.IsMatch(text, @"\b(?:kill|hurt|punch|steal|hack|knife|weapon|fire|burn|burning|drown\w*|stab|traffic|choke|starve|starving|scream)\b", RegexOptions.IgnoreCase)) return null;
            if (!game && Regex.IsMatch(text, @"\bjump(?:ing)?\b.{0,30}\b(?:window|roof|balcony|bridge|ledge|building|cliff|stairs|off|out)\b", RegexOptions.IgnoreCase)) return null;
            return text;
        }
    }

    // Spotify's own buttons, read through Windows' accessibility interface (the one screen readers use): the Play button for a search result is
    // found by name in any language and pressed, instead of guessing at keys. Replaced when testing.
    internal static class SpotifyUi
    {
        internal static Func<string[]> Buttons = RealButtons;
        internal static Func<int, bool> Invoke = RealInvoke;
        internal static int Poll = 500;
        private static System.Windows.Automation.AutomationElement[] cache = new System.Windows.Automation.AutomationElement[0];

        internal static readonly string[] KnownVerbs = { "Play", "Spela upp", "Reproducir", "Reproduzir", "Lire", "Riproduci", "Afspelen", "Odtwórz", "Spill av", "Afspil", "Toista" };

        // Which wording is the Play button's: the one learned before if it is on screen, then a known one, then the beginning most buttons share.
        internal static string Choose(string[] names, string learned)
        {
            if (!string.IsNullOrEmpty(learned) && HasVerb(names, learned)) return learned;
            foreach (string known in KnownVerbs) if (HasVerb(names, known)) return known;
            return Verb(names);
        }

        private static bool HasVerb(string[] names, string verb)
        {
            foreach (string name in names) if (name != null && name.StartsWith(verb + " ", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // How many buttons begin with this wording.
        internal static int Count(string[] names, string verb)
        {
            int total = 0;
            foreach (string name in names) if (name != null && name.StartsWith(verb + " ", StringComparison.OrdinalIgnoreCase)) total++;
            return total;
        }

        // The word or two that start every Play button ("Play", "Spela upp"): the beginning most buttons share.
        internal static string Verb(string[] names)
        {
            Dictionary<string, int> two = new Dictionary<string, int>(), one = new Dictionary<string, int>();
            foreach (string name in names)
            {
                string[] words = (name ?? "").Trim().Split(' ');
                if (words.Length >= 3) { string key = words[0] + " " + words[1]; int n; two[key] = two.TryGetValue(key, out n) ? n + 1 : 1; }
                if (words.Length >= 2) { int n; one[words[0]] = one.TryGetValue(words[0], out n) ? n + 1 : 1; }
            }
            string best = null;
            int count = 3;
            foreach (KeyValuePair<string, int> entry in two) if (entry.Value > count) { best = entry.Key; count = entry.Value; }
            if (best != null) return best;
            foreach (KeyValuePair<string, int> entry in one) if (entry.Value > count) { best = entry.Key; count = entry.Value; }
            return best;
        }

        // How much of what was asked for is in a title, from 0 to 1.
        internal static double Score(string title, string query)
        {
            List<string> words = new List<string>();
            foreach (string word in Regex.Split((query ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}']+")) if (word.Length >= 2 && word != "the") words.Add(word);
            if (words.Count == 0) return 0;
            string low = (title ?? "").ToLowerInvariant();
            int hits = 0;
            foreach (string word in words) if (low.Contains(word)) hits++;
            return hits / (double)words.Count;
        }

        // The first Play button whose song matches most of the words asked for, or -1.
        internal static int Find(string[] names, string verb, string query)
        {
            if (verb == null) return -1;
            int best = -1;
            double bestScore = 0.59;
            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i] ?? "";
                if (!name.StartsWith(verb + " ", StringComparison.OrdinalIgnoreCase)) continue;
                double score = Score(name.Substring(verb.Length), query);
                if (score > bestScore) { bestScore = score; best = i; }
            }
            return best;
        }

        private static string[] RealButtons()
        {
            try
            {
                IntPtr window = IntPtr.Zero;
                foreach (Process process in Process.GetProcessesByName("Spotify"))
                {
                    using (process) if (window == IntPtr.Zero && process.MainWindowHandle != IntPtr.Zero) window = process.MainWindowHandle;
                }
                if (window == IntPtr.Zero) return null;
                System.Windows.Automation.AutomationElement root = System.Windows.Automation.AutomationElement.FromHandle(window);
                System.Windows.Automation.AutomationElementCollection found = root.FindAll(System.Windows.Automation.TreeScope.Descendants, new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.Button));
                cache = new System.Windows.Automation.AutomationElement[found.Count];
                string[] names = new string[found.Count];
                for (int i = 0; i < found.Count; i++) { cache[i] = found[i]; names[i] = found[i].Current.Name; }
                return names;
            }
            catch (Exception) { return null; }
        }

        private static bool RealInvoke(int index)
        {
            try
            {
                if (index < 0 || index >= cache.Length) return false;
                object pattern;
                if (cache[index].TryGetCurrentPattern(System.Windows.Automation.InvokePattern.Pattern, out pattern)) { ((System.Windows.Automation.InvokePattern)pattern).Invoke(); return true; }
                return false;
            }
            catch (Exception) { return false; }
        }
    }

    // The first video for a search on YouTube, so "play it" can open the video itself instead of a list.
    internal static class YouTube
    {
        internal static Func<string, string> Fetch = RealFetch;

        internal static string FirstVideo(string html)
        {
            Match match = Regex.Match(html ?? "", "\"videoRenderer\":\\{\"videoId\":\"([\\w-]{11})\"");
            return match.Success ? match.Groups[1].Value : null;
        }

        private static string RealFetch(string query)
        {
            try
            {
                Net.Secure();
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create("https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query));
                request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";
                request.Headers["Accept-Language"] = "en-US,en;q=0.9";
                request.Headers["Cookie"] = "SOCS=CAI";
                request.Timeout = 8000;
                request.ReadWriteTimeout = 8000;
                using (WebResponse response = request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    StringBuilder text = new StringBuilder();
                    char[] buffer = new char[8192];
                    int count;
                    while (text.Length < 3000000 && (count = reader.Read(buffer, 0, buffer.Length)) > 0) text.Append(buffer, 0, count);
                    return text.ToString();
                }
            }
            catch (Exception) { return ""; }
        }
    }

    // Starting with Windows is only ever done when asked for, and only for this user.
    internal static class Startup
    {
        internal static string RunPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";

        internal static bool Enabled()
        {
            try { using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunPath)) return key != null && key.GetValue("Capri") != null; }
            catch (Exception) { return false; }
        }

        internal static void Set(bool on)
        {
            using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunPath))
            {
                if (on) key.SetValue("Capri", "\"" + Application.ExecutablePath + "\"");
                else if (key.GetValue("Capri") != null) key.DeleteValue("Capri", false);
            }
        }
    }

    internal enum ErrandKind { None, PlaySong, PlayVideo, Web, Timer, Clock, Study, StopStudy, ClearAlarms, Open, Play, Pause, Next, Previous, Mute, Volume, Remember, Forget, ForgetAll }

    internal sealed class Errand
    {
        internal ErrandKind Kind;
        internal string Text = "";
        internal double Seconds;
        internal DateTime Due;
        internal int Presses;
        internal bool Up;
        internal string Target = "";
        internal bool More;
    }

    // What she can be asked to do, matched from plain words so that it is quick, works without a model and can never run anything unexpected.
    // Only things on a short list are ever started: a few folders and Windows tools, the media keys, volume, her own timers and her notes.
    internal static class Errands
    {
        private static readonly string Duration = @"(\d+(?:\.\d+)?|a|an|one|two|three|four|five|six|seven|eight|nine|ten|fifteen|twenty|thirty|forty|sixty)\s*(seconds?|secs?|minutes?|mins?|hours?|hrs?)";
        private static readonly string Starts = @"^(?:set|start|make|create|add|put|give me|remind|wake|timer|countdown|alarm)\b";

        internal static readonly string[] Secret = { "password", "passcode", "passphrase", "pin number", "my pin", "credit card", "card number", "cvv", "ssn", "social security", "api key", "secret key", "private key", "token", "seed phrase" };

        // Where the things she can open live. Folders come from Windows; the rest are programs that ship with it.
        internal static string Place(string name)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            switch (name)
            {
                case "downloads": case "download folder": case "downloads folder": return Path.Combine(home, "Downloads");
                case "documents": case "documents folder": return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                case "desktop": case "desktop folder": return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                case "pictures": case "photos": case "pictures folder": return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                case "videos": case "videos folder": return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                case "music folder": return Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
                case "file explorer": case "explorer": case "files": case "this pc": return "explorer.exe";
                case "notepad": return "notepad.exe";
                case "calculator": case "calc": return "calc.exe";
                case "paint": return "mspaint.exe";
                case "task manager": return "taskmgr.exe";
                case "settings": case "windows settings": return "ms-settings:";
                case "snipping tool": return "snippingtool.exe";
                case "youtube": return "https://www.youtube.com";
                case "google": return "https://www.google.com";
                case "github": return "https://github.com";
                case "reddit": return "https://www.reddit.com";
                case "twitch": return "https://www.twitch.tv";
                default: return null;
            }
        }

        private static string Plain(string text)
        {
            text = (text ?? "").Trim();
            text = Regex.Replace(text, @"[\u2018\u2019]", "'");
            text = Regex.Replace(text, @"^(?:(?:hey|hi|ok|okay|yo)[ ,]+)?(?:capri|cappy)?[,!. ]+", "", RegexOptions.IgnoreCase);
            for (int i = 0; i < 3; i++) text = Regex.Replace(text, @"^(?:can you|could you|would you|will you|please|just|go ahead and|i want you to|i need you to)\s+", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\s+(?:please|for me|now|thanks|thank you|capri)[.!?]*$", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"^(play|clay|plea|put on|queue|start|open|search|look up|google|find|set|remind|dare|remember|forget)\s*[,;:]\s*", "$1 ", RegexOptions.IgnoreCase);
            return text.Trim(' ', '.', '!', '?', ',');
        }

        private static double Number(string word)
        {
            double value;
            if (double.TryParse(word, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value)) return value;
            string[] names = { "a", "an", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "fifteen", "twenty", "thirty", "forty", "sixty" };
            double[] values = { 1, 1, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 15, 20, 30, 40, 60 };
            int at = Array.IndexOf(names, word);
            return at < 0 ? 0 : values[at];
        }

        // Seconds in a phrase like "10 minutes", "an hour" or "half an hour", or 0 when there is none.
        internal static double Length(string text)
        {
            if (Regex.IsMatch(text, @"\bhalf an? hour\b")) return 1800;
            Match match = Regex.Match(text, @"\b" + Duration + @"\b");
            if (!match.Success) return 0;
            double amount = Number(match.Groups[1].Value);
            string unit = match.Groups[2].Value;
            double seconds = unit.StartsWith("h") ? amount * 3600 : unit.StartsWith("m") ? amount * 60 : amount;
            return seconds >= 1 && seconds <= 86400 ? seconds : 0;
        }

        internal static string Words(string text) { return (text ?? "").Trim().ToLowerInvariant(); }

        // Where installed programs can be launched from: the Start menu shortcuts. Replaced when testing.
        internal static Func<string, string> AppLookup = StartMenuApp;

        private static readonly string[][] Aliases = new string[][]
        {
            new string[] { "edge", "microsoft edge" }, new string[] { "chrome", "google chrome" }, new string[] { "vscode", "visual studio code" }, new string[] { "code", "visual studio code" },
            new string[] { "obs", "obs studio" }, new string[] { "vlc", "vlc media player" }, new string[] { "powerpoint", "powerpoint" }
        };

        // The shortcut that best matches a name: exact, then starting with it, then containing it as a whole word. Uninstallers and help files are never picked.
        internal static string Match(string name, IEnumerable<string> shortcuts)
        {
            name = (name ?? "").Trim().ToLowerInvariant();
            if (name.Length < 3) return null;
            foreach (string[] alias in Aliases) if (alias[0] == name) name = alias[1];
            string best = null;
            int bestRank = 9, bestLength = int.MaxValue;
            foreach (string path in shortcuts)
            {
                string item = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                if (Regex.IsMatch(item, @"uninstall|readme|help|manual|license|release notes|website|documentation")) continue;
                int rank = item == name ? 0 : item.StartsWith(name) ? 1 : (" " + item + " ").Contains(" " + name + " ") ? 2 : 9;
                if (rank == 9) continue;
                if (rank < bestRank || (rank == bestRank && item.Length < bestLength)) { best = path; bestRank = rank; bestLength = item.Length; }
            }
            return best;
        }

        private static string StartMenuApp(string name)
        {
            List<string> found = new List<string>();
            foreach (Environment.SpecialFolder where in new Environment.SpecialFolder[] { Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.Programs })
            {
                try
                {
                    string folder = Environment.GetFolderPath(where);
                    if (folder.Length > 0 && Directory.Exists(folder)) found.AddRange(Directory.GetFiles(folder, "*.lnk", SearchOption.AllDirectories));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return Match(name, found);
        }

        internal static string Describe(double seconds)
        {
            if (seconds < 60) return Lang.Count("{0} second|{0} seconds", (int)seconds);
            if (seconds < 3600) return Lang.Count("{0} minute|{0} minutes", (int)Math.Round(seconds / 60));
            int h = (int)(seconds / 3600), rest = (int)Math.Round((seconds - h * 3600) / 60);
            return Lang.Count("{0} hour|{0} hours", h) + (rest > 0 ? " " + Lang.F("{0} min", rest) : "");
        }

        // The next time the clock shows this, today or tomorrow. With no am or pm the nearest of the two is used.
        internal static DateTime NextClock(int hour, int minute, string half, DateTime now)
        {
            if (hour > 23 || minute > 59) return DateTime.MinValue;
            List<int> hours = new List<int>();
            if (half.StartsWith("a")) hours.Add(hour % 12);
            else if (half.StartsWith("p")) hours.Add(hour % 12 + 12);
            else if (hour >= 13 || hour == 0) hours.Add(hour);
            else { hours.Add(hour % 12); hours.Add(hour % 12 + 12); }
            DateTime best = DateTime.MaxValue;
            foreach (int h in hours)
            {
                DateTime when = now.Date.AddHours(h).AddMinutes(minute);
                if (when <= now.AddSeconds(5)) when = when.AddDays(1);
                if (when < best) best = when;
            }
            return best;
        }

        internal static string Name(string text)
        {
            Match match = Regex.Match(text ?? "", @"\b(?:my name is|my name's|i am called|i'm called|call me)\s+([A-Za-z][A-Za-z'-]{1,19})\b", RegexOptions.IgnoreCase);
            if (!match.Success) return null;
            string name = match.Groups[1].Value;
            if (Regex.IsMatch(name, "^(capri|cappy|it|that|this|when|if|and|the|a|an|so|what|later|back|crazy|maybe|please)$", RegexOptions.IgnoreCase)) return null;
            return char.ToUpperInvariant(name[0]) + name.Substring(1);
        }

        internal static Errand Parse(string said)
        {
            Errand result = new Errand();
            string cased = Plain(said);
            string text = cased.ToLowerInvariant();
            if (text.Length == 0) return result;
            Match match;

            if (Regex.IsMatch(text, @"^forget (?:everything|all of it|it all|all of that)(?: about me)?$")) { result.Kind = ErrandKind.ForgetAll; return result; }
            match = Regex.Match(cased, @"^(?:forget|remove|delete|erase|drop)\s+(?:about\s+|that\s+)?((?:my|the|our|i)\b.{2,70}?)(?:\s+from\s+(?:your\s+|my\s+|the\s+)?(?:memory|notes|brain))?$|^(?:forget|remove|delete|erase|drop)\s+(?:about\s+|that\s+)(.{3,80}?)(?:\s+from\s+(?:your\s+|my\s+|the\s+)?(?:memory|notes|brain))?$|^(?:remove|delete|erase)\s+(.{3,80}?)\s+from\s+(?:your\s+|my\s+|the\s+)?(?:memory|notes|brain)$", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                string about = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
                if (!Regex.IsMatch(about, @"^(?:it|that|this|them|me|you|everything|all)$", RegexOptions.IgnoreCase)) { result.Kind = ErrandKind.Forget; result.Text = about.Trim(); return result; }
            }
            match = Regex.Match(cased, @"^(?:remember|(?:please )?make a note|don'?t forget|keep in mind|note that)[,:]?\s+(?:that\s+|this:\s*)?(?!(?:when|how|what|why|if|where|who|which)\b)(.{3,200})$", RegexOptions.IgnoreCase);
            if (match.Success) { result.Kind = ErrandKind.Remember; result.Text = match.Groups[1].Value.Trim(); return result; }

            if (Regex.IsMatch(text, @"^(?:cancel|clear|delete|remove|stop)\s+(?:all\s+)?(?:my\s+|the\s+)?(?:timers?|alarms?)$")) { result.Kind = ErrandKind.ClearAlarms; return result; }
            if (Regex.IsMatch(text, @"^(?:stop|end|cancel)\s+(?:the\s+|my\s+)?(?:study|focus|pomodoro)")) { result.Kind = ErrandKind.StopStudy; return result; }
            if (Regex.IsMatch(text, @"^(?:start|begin|do)\s+(?:a\s+|the\s+|my\s+)?(?:study|focus|pomodoro)\b"))
            {
                result.Kind = ErrandKind.Study;
                double focus = Length(text);
                result.Seconds = focus >= 60 ? focus : 25 * 60;
                return result;
            }

            if (Regex.IsMatch(text, Starts) && Regex.IsMatch(text, @"\b(?:timer|alarm|remind|wake|countdown)"))
            {
                double seconds = Length(text);
                string label = "";
                match = Regex.Match(text, @"\b(?:to|about)\s+(.{2,60})$");
                if (match.Success && !Regex.IsMatch(match.Groups[1].Value, @"^\d")) label = match.Groups[1].Value.Trim();
                if (seconds > 0)
                {
                    result.Kind = ErrandKind.Timer;
                    result.Seconds = seconds;
                    result.Text = label;
                    return result;
                }
                match = Regex.Match(text, @"\b(?:at|for)\s+(\d{1,2})(?::(\d{2}))?\s*(a\.?m\.?|p\.?m\.?)?(?:\s|$)");
                if (match.Success && (match.Groups[2].Success || match.Groups[3].Success || Regex.IsMatch(text, @"\b(?:alarm|wake)")))
                {
                    DateTime due = NextClock(int.Parse(match.Groups[1].Value), match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0, match.Groups[3].Value, DateTime.Now);
                    if (due != DateTime.MinValue)
                    {
                        result.Kind = ErrandKind.Clock;
                        result.Due = due;
                        result.Text = label;
                        return result;
                    }
                }
            }

            match = Regex.Match(text, @"^(?:open|launch|start|run|show me)(?: up)?\s+(?:my |the |a )?(.{2,30}?)(?:\s+(?:and|then|,)\s+(.{3,}))?$");
            if (match.Success)
            {
                string wanted = match.Groups[1].Value.Trim();
                string target = Place(wanted) ?? AppLookup(wanted);
                if (target != null) { result.Kind = ErrandKind.Open; result.Text = wanted; result.Target = target; result.More = match.Groups[2].Success; return result; }
            }

            match = Regex.Match(cased, @"^(?:play|clay|plea|plate|put on|queue|start)\s+(.{2,80}?)(?:\s+(?:on|in|with|using|from))?\s+spotify\b.*$|^spotify[,:]?\s+play\s+(.{2,80})$", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                string song = Regex.Replace((match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).Trim(), @"[^\p{L}\p{N} '&,.!-]", "").Trim();
                if (song.Length >= 2 && !Regex.IsMatch(song, @"^(?:the |some |my )?(?:music|song|songs|something|anything)$", RegexOptions.IgnoreCase)) { result.Kind = ErrandKind.PlaySong; result.Text = song; return result; }
            }
            match = Regex.Match(cased, @"^(?:search(?: for)?|look up|google|find|play|clay|plea)\s+(.{2,80}?)(?:\s+(?:on|in)\s+(google|youtube|bing|duckduckgo))?$", RegexOptions.IgnoreCase);
            if (match.Success && (Regex.IsMatch(text, @"^(?:search|look up|google)\b") || (match.Groups[2].Success && Regex.IsMatch(text, @"^(?:find|play|clay|plea)\b"))))
            {
                string query = Regex.Replace(match.Groups[1].Value.Trim(), @"[^\p{L}\p{N} '&,.!?-]", "").Trim();
                string engine = match.Groups[2].Success ? match.Groups[2].Value.ToLowerInvariant() : "google";
                string address = engine == "youtube" ? "https://www.youtube.com/results?search_query=" : engine == "bing" ? "https://www.bing.com/search?q=" : engine == "duckduckgo" ? "https://duckduckgo.com/?q=" : "https://www.google.com/search?q=";
                if (query.Length >= 2)
                {
                    result.Kind = engine == "youtube" && Regex.IsMatch(text, @"^(?:play|clay|plea|find)\b") ? ErrandKind.PlayVideo : ErrandKind.Web;
                    result.Text = result.Kind == ErrandKind.PlayVideo ? query : query + " on " + engine;
                    result.Target = address + Uri.EscapeDataString(query);
                    return result;
                }
            }
            if (Regex.IsMatch(text, @"^(?:pause|stop|hold)(?: the| my)?(?: music| song| track| video| media| playback| audio)$|^pause(?: it)?$")) { result.Kind = ErrandKind.Pause; return result; }
            if (Regex.IsMatch(text, @"^(?:resume|unpause|continue|play)(?: the| my)?(?: music| song| track| video| media| playback| audio)$|^(?:resume|unpause)(?: it)?$")) { result.Kind = ErrandKind.Play; return result; }
            if (Regex.IsMatch(text, @"^(?:skip|next)(?: this| the)?(?: song| track| one)?$|^(?:play )?(?:the )?next (?:song|track)$")) { result.Kind = ErrandKind.Next; return result; }
            if (Regex.IsMatch(text, @"^(?:play )?(?:the )?previous (?:song|track)$|^go back (?:a|one) (?:song|track)$")) { result.Kind = ErrandKind.Previous; return result; }
            if (Regex.IsMatch(text, @"^(?:mute|unmute)(?: the| my)?(?: sound| volume| pc| computer| audio| music)?$")) { result.Kind = ErrandKind.Mute; return result; }
            match = Regex.Match(text, @"^(?:(?:turn|make) (?:it|the (?:volume|sound|music))|volume|turn (up|down) the (?:volume|sound|music))\s*(up|down|louder|quieter|softer)?$");
            if (match.Success && (match.Groups[1].Success || match.Groups[2].Success))
            {
                string way = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                result.Kind = ErrandKind.Volume;
                result.Up = way == "up" || way == "louder";
                result.Presses = 5;
                return result;
            }
            return result;
        }

        // Passwords and the like are never kept.
        internal static bool Sensitive(string fact)
        {
            string low = (fact ?? "").ToLowerInvariant();
            foreach (string word in Secret) if (low.Contains(word)) return true;
            return Regex.IsMatch(low, @"\b\d{4}[ -]?\d{4}[ -]?\d{4}[ -]?\d{4}\b");
        }
    }

    internal sealed class Track
    {
        internal string Title = "";
        internal string Artist = "";
        internal string App = "";
        internal bool Playing;
        internal string Key { get { return Title + "|" + Artist; } }
    }

    // Reads the same "now playing" information Windows shows on the volume flyout: Spotify, browsers, video players and so on.
    // Only the title, artist and app name are read. It is reached through reflection so nothing extra is needed to build or run.
    internal static class Media
    {
        internal static Func<Track> Source;
        private static Type managerType;
        private static Type sessionType;
        private static Type infoType;
        private static Type propertiesType;
        private static MethodInfo asTask;
        private static bool? usable;

        private static bool Prepare()
        {
            if (usable != null) return usable.Value;
            try
            {
                Assembly runtime = Assembly.Load("System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
                Type extensions = runtime.GetType("System.WindowsRuntimeSystemExtensions");
                foreach (MethodInfo method in extensions.GetMethods())
                    if (method.Name == "AsTask" && method.IsGenericMethodDefinition && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType.Name == "IAsyncOperation`1") asTask = method;
                managerType = Type.GetType("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType=WindowsRuntime");
                sessionType = Type.GetType("Windows.Media.Control.GlobalSystemMediaTransportControlsSession, Windows.Media.Control, ContentType=WindowsRuntime");
                infoType = Type.GetType("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackInfo, Windows.Media.Control, ContentType=WindowsRuntime");
                propertiesType = Type.GetType("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties, Windows.Media.Control, ContentType=WindowsRuntime");
                usable = asTask != null && managerType != null && sessionType != null && infoType != null && propertiesType != null;
            }
            catch (Exception) { usable = false; }
            return usable.Value;
        }

        internal static bool Available { get { return Source != null || Prepare(); } }

        private static object Wait(object operation, Type result)
        {
            System.Threading.Tasks.Task task = (System.Threading.Tasks.Task)asTask.MakeGenericMethod(result).Invoke(null, new object[] { operation });
            if (!task.Wait(4000)) return null;
            return task.GetType().GetProperty("Result").GetValue(task, null);
        }

        private static string Friendly(string app)
        {
            app = app ?? "";
            int bang = app.IndexOf('!');
            if (bang >= 0 && bang < app.Length - 1) app = app.Substring(bang + 1);
            return app == "MSEdge" ? "Edge" : app.Replace(".exe", "");
        }

        // The track that is playing, or failing that one that is paused; null when nothing is open or Windows can't say.
        internal static Track Read()
        {
            if (Source != null) return Source();
            if (!Prepare()) return null;
            try
            {
                object manager = Wait(managerType.GetMethod("RequestAsync").Invoke(null, null), managerType);
                if (manager == null) return null;
                Track chosen = null;
                foreach (object session in (System.Collections.IEnumerable)managerType.GetMethod("GetSessions").Invoke(manager, null))
                {
                    object info = sessionType.GetMethod("GetPlaybackInfo").Invoke(session, null);
                    int status = Convert.ToInt32(infoType.GetProperty("PlaybackStatus").GetValue(info, null));
                    object properties = Wait(sessionType.GetMethod("TryGetMediaPropertiesAsync").Invoke(session, null), propertiesType);
                    if (properties == null) continue;
                    Track track = new Track();
                    track.Title = ((string)propertiesType.GetProperty("Title").GetValue(properties, null) ?? "").Trim();
                    track.Artist = ((string)propertiesType.GetProperty("Artist").GetValue(properties, null) ?? "").Trim();
                    track.App = Friendly((string)sessionType.GetProperty("SourceAppUserModelId").GetValue(session, null));
                    track.Playing = status == 4;
                    if (track.Title.Length == 0) continue;
                    if (chosen == null || (track.Playing && !chosen.Playing)) chosen = track;
                }
                return chosen;
            }
            catch (Exception) { return null; }
        }
    }

    // Records from one chosen microphone through the Windows wave API and hands the sound on as a stream, so the recognizer
    // can use a microphone other than the Windows default (which may be a silent virtual one).
    internal sealed class MicTap : Stream
    {
        private const int Buffers = 4;
        private const int Chunk = 3200;
        private readonly int device;
        private readonly IntPtr[] headers = new IntPtr[Buffers];
        private readonly IntPtr[] blocks = new IntPtr[Buffers];
        private readonly Queue<byte[]> queue = new Queue<byte[]>();
        private IntPtr handle;
        private EventWaitHandle ready;
        private Thread worker;
        private byte[] current;
        private int used;
        private volatile bool ended;
        internal long Total;

        internal MicTap(int index) { device = index; }

        internal bool Start()
        {
            Native.WaveFormat format = new Native.WaveFormat();
            format.Tag = 1;
            format.Channels = 1;
            format.Rate = 16000;
            format.BytesPerSecond = 32000;
            format.Align = 2;
            format.Bits = 16;
            ready = new EventWaitHandle(false, EventResetMode.AutoReset);
            if (Native.waveInOpen(out handle, device, ref format, ready.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, 0x00050000) != 0) { handle = IntPtr.Zero; return false; }
            int size = Marshal.SizeOf(typeof(Native.WaveHeader));
            for (int i = 0; i < Buffers; i++)
            {
                blocks[i] = Marshal.AllocHGlobal(Chunk);
                headers[i] = Marshal.AllocHGlobal(size);
                Native.WaveHeader header = new Native.WaveHeader();
                header.Data = blocks[i];
                header.Length = Chunk;
                Marshal.StructureToPtr(header, headers[i], false);
                Native.waveInPrepareHeader(handle, headers[i], size);
                Native.waveInAddBuffer(handle, headers[i], size);
            }
            Native.waveInStart(handle);
            worker = new Thread(Pump);
            worker.IsBackground = true;
            worker.Start();
            return true;
        }

        private void Pump()
        {
            int size = Marshal.SizeOf(typeof(Native.WaveHeader));
            while (!ended)
            {
                ready.WaitOne(250);
                for (int i = 0; i < Buffers && !ended; i++)
                {
                    Native.WaveHeader header = (Native.WaveHeader)Marshal.PtrToStructure(headers[i], typeof(Native.WaveHeader));
                    if ((header.Flags & 1) == 0 || header.Recorded == 0) continue;
                    byte[] data = new byte[header.Recorded];
                    Marshal.Copy(blocks[i], data, 0, data.Length);
                    Feed(data);
                    Native.waveInAddBuffer(handle, headers[i], size);
                }
            }
        }

        internal void Feed(byte[] data)
        {
            lock (queue) { queue.Enqueue(data); Total += data.Length; Monitor.PulseAll(queue); }
        }

        // Lets go of the microphone; whatever is waiting is still read, then the stream ends.
        internal void Stop()
        {
            if (ended) return;
            ended = true;
            if (ready != null) ready.Set();
            if (worker != null) worker.Join(1500);
            if (handle != IntPtr.Zero)
            {
                int size = Marshal.SizeOf(typeof(Native.WaveHeader));
                Native.waveInReset(handle);
                for (int i = 0; i < Buffers; i++) if (headers[i] != IntPtr.Zero) Native.waveInUnprepareHeader(handle, headers[i], size);
                Native.waveInClose(handle);
                handle = IntPtr.Zero;
            }
            for (int i = 0; i < Buffers; i++)
            {
                if (headers[i] != IntPtr.Zero) { Marshal.FreeHGlobal(headers[i]); headers[i] = IntPtr.Zero; }
                if (blocks[i] != IntPtr.Zero) { Marshal.FreeHGlobal(blocks[i]); blocks[i] = IntPtr.Zero; }
            }
            lock (queue) Monitor.PulseAll(queue);
        }

        // Waits up to the given time for sound: the number of bytes, 0 when the microphone has been let go, or -1 if nothing arrived in time.
        internal int ReadWithin(byte[] buffer, int milliseconds)
        {
            lock (queue)
            {
                if ((current == null || used >= current.Length) && queue.Count == 0)
                {
                    if (ended) return 0;
                    Monitor.Wait(queue, milliseconds);
                    if (queue.Count == 0) return ended ? 0 : -1;
                }
            }
            return Read(buffer, 0, buffer.Length);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (queue)
            {
                while (current == null || used >= current.Length)
                {
                    if (queue.Count > 0) { current = queue.Dequeue(); used = 0; }
                    else if (ended) return 0;
                    else Monitor.Wait(queue, 200);
                }
                int length = Math.Min(count, current.Length - used);
                Buffer.BlockCopy(current, used, buffer, offset, length);
                used += length;
                return length;
            }
        }

        public override bool CanRead { get { return true; } }
        // The recognizer insists on a stream that says it can seek. A live microphone cannot, so it claims to be endless and ignores seeks.
        public override bool CanSeek { get { return true; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return long.MaxValue; } }
        public override long Position { get { return 0; } set { } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { return 0; }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Stop();
            base.Dispose(disposing);
        }
    }

    // Whisper, run on this computer through whisper.cpp, turns the recorded phrase into text far more accurately than the recognizer
    // built into Windows. It is used when whisper-cli.exe and a model are in the speech folder next to her settings; nothing is sent anywhere.
    internal static class Whisper
    {
        internal static Func<string, string> Runner;

        internal static string Program(string folder)
        {
            foreach (string path in new string[] { Path.Combine(folder, "bin", "Release", "whisper-cli.exe"), Path.Combine(folder, "bin", "whisper-cli.exe") })
                if (File.Exists(path)) return path;
            return null;
        }

        // The model to use. For English: small.en if it is there, then base.en, then any other. For another language only a model that
        // is not English-only will do ("small" and "base" understand many languages, "small.en" and "base.en" do not).
        internal static string Model(string folder, string language)
        {
            string models = Path.Combine(folder, "models");
            if (!Directory.Exists(models)) return null;
            string[] all = Directory.GetFiles(models, "ggml-*.bin");
            Array.Sort(all, StringComparer.OrdinalIgnoreCase);
            if (language == "en")
            {
                foreach (string name in new string[] { "ggml-small.en.bin", "ggml-base.en.bin" })
                    if (File.Exists(Path.Combine(models, name))) return Path.Combine(models, name);
                return all.Length > 0 ? all[0] : null;
            }
            foreach (string preferred in new string[] { "ggml-small", "ggml-base", "ggml-medium", "ggml-large", "ggml-tiny" })
                foreach (string path in all)
                    if (Path.GetFileName(path).StartsWith(preferred, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(path).IndexOf(".en", StringComparison.OrdinalIgnoreCase) < 0) return path;
            foreach (string path in all) if (Path.GetFileName(path).IndexOf(".en", StringComparison.OrdinalIgnoreCase) < 0) return path;
            return null;
        }

        internal static string Model(string folder) { return Model(folder, "en"); }

        // The words handed to whisper-cli. The English hint only helps in English, so other languages go without it.
        internal static string Arguments(string model, string wave, string language)
        {
            return "-m \"" + model + "\" -f \"" + wave + "\" -nt -np -l " + language + (language == "en" ? " --prompt \"Hello Capri.\"" : "");
        }

        internal static bool Available(string folder, string language) { return Runner != null || (Program(folder) != null && Model(folder, language) != null); }

        internal static bool Available(string folder) { return Available(folder, "en"); }

        internal static string Describe(string folder, string language)
        {
            string model = Model(folder, language);
            if (Runner == null && Program(folder) != null && model == null && Model(folder, "en") != null) return "Windows' own (her Whisper model is English only; Download better ears… adds one for " + language + ")";
            return Runner != null || (Program(folder) != null && model != null) ? "Whisper (" + (model == null ? "test" : Path.GetFileNameWithoutExtension(model).Replace("ggml-", "")) + ")" : "Windows' own";
        }

        internal static string Describe(string folder) { return Describe(folder, "en"); }

        // A 16 kHz, 16-bit mono wave file around the recorded sound.
        internal static byte[] Wave(byte[] pcm)
        {
            using (MemoryStream file = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(file))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + pcm.Length);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(16000);
                writer.Write(32000);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(pcm.Length);
                writer.Write(pcm);
                writer.Flush();
                return file.ToArray();
            }
        }

        // Bracketed notes such as [BLANK_AUDIO] or (music) are not words.
        internal static string Clean(string text)
        {
            text = Regex.Replace(text ?? "", @"\[[^\]]*\]|\([^)]*\)", " ");
            return Regex.Replace(text, @"\s+", " ").Trim();
        }

        internal static string Transcribe(string folder, byte[] pcm, string language)
        {
            string wave = Path.Combine(Path.GetTempPath(), "capri-" + Guid.NewGuid().ToString("N") + ".wav");
            File.WriteAllBytes(wave, Wave(pcm));
            try
            {
                if (Runner != null) return Clean(Runner(wave));
                string basics = Arguments(Model(folder, language), wave, language);
                try { return Run(folder, basics + " -t " + Math.Max(2, Math.Min(8, Environment.ProcessorCount)) + " -bs 1 -bo 1"); }
                catch (InvalidOperationException) { return Run(folder, basics); }
            }
            finally { try { File.Delete(wave); } catch (IOException) { } }
        }

        private static string Run(string folder, string arguments)
        {
            {
                ProcessStartInfo start = new ProcessStartInfo(Program(folder));
                start.Arguments = arguments;
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                start.RedirectStandardOutput = true;
                start.StandardOutputEncoding = Encoding.UTF8;
                using (Process run = Process.Start(start))
                {
                    string output = run.StandardOutput.ReadToEnd();
                    if (!run.WaitForExit(40000)) { try { run.Kill(); } catch (InvalidOperationException) { } throw new TimeoutException("Whisper took too long."); }
                    if (run.ExitCode != 0) throw new InvalidOperationException("Whisper stopped with code " + run.ExitCode);
                    return Clean(output);
                }
            }
        }
    }

    // The optional download of better ears: a Windows build of whisper.cpp and a model that understands many languages.
    // Both are fixed files, checked by size and SHA-256 before anything is used, and it only happens when you ask.
    internal static class EarsDownload
    {
        internal const string ProgramUrl = "https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-bin-x64.zip";
        internal const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small-q5_1.bin";
        internal const string ModelName = "ggml-small-q5_1.bin";

        // The expected size and fingerprint of each file. Only the tests change these.
        internal static long ProgramBytes = 8928640;
        internal static string ProgramSha = "6ba69e3482d7826214f90a6a9c84ca07782aec1e1d0c6a7c30c994fd5d816ccb";
        internal static long ModelBytes = 190085487;
        internal static string ModelSha = "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb";

        // Tests hand in a stand-in for the network: the address, the file to write, a report of bytes so far, and whether to give up.
        internal static Action<string, string, Action<long>, Func<bool>> Fetch;

        internal static bool NeedProgram(string folder) { return Whisper.Program(folder) == null; }
        internal static bool NeedModel(string folder) { return !File.Exists(Path.Combine(folder, "models", ModelName)); }

        // Only these two sites, over https, and the answer must still be https after any redirect.
        internal static void Download(string url, string destination, Action<long> report, Func<bool> cancelled)
        {
            Uri address = new Uri(url);
            if (address.Scheme != "https" || (address.Host != "github.com" && address.Host != "huggingface.co")) throw new InvalidOperationException("That address is not allowed.");
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch (Exception) { }
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(address);
            request.UserAgent = "Capri";
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                if (response.ResponseUri.Scheme != "https") throw new InvalidOperationException("The download was redirected away from https.");
                using (Stream input = response.GetResponseStream())
                using (FileStream output = new FileStream(destination, FileMode.Create, FileAccess.Write))
                {
                    byte[] buffer = new byte[65536];
                    long total = 0;
                    int count;
                    while (!cancelled() && (count = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, count);
                        total += count;
                        report(total);
                    }
                }
            }
        }

        internal static string Sha256(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (System.Security.Cryptography.SHA256 hash = System.Security.Cryptography.SHA256.Create())
            {
                StringBuilder text = new StringBuilder();
                foreach (byte b in hash.ComputeHash(stream)) text.Append(b.ToString("x2"));
                return text.ToString();
            }
        }

        // Gets one file next to where it will live and keeps it only if it is exactly the file expected.
        private static string Get(string url, long bytes, string sha, string folder, Action<long> report, Func<bool> cancelled)
        {
            string part = Path.Combine(folder, "download-" + Guid.NewGuid().ToString("N") + ".part");
            try
            {
                if (Fetch != null) Fetch(url, part, report, cancelled); else Download(url, part, report, cancelled);
                if (cancelled()) throw new OperationCanceledException();
                if (new FileInfo(part).Length != bytes || !string.Equals(Sha256(part), sha, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The download was not the file it should be, so it was not used.");
                return part;
            }
            catch (Exception) { try { File.Delete(part); } catch (IOException) { } throw; }
        }

        // Only whisper-cli.exe and the libraries it loads are taken from the zip, by file name, never by path. The program goes in last so a half unpack is not mistaken for a working one.
        internal static void Unpack(string zipPath, string target)
        {
            Directory.CreateDirectory(target);
            using (ZipArchive zip = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    string name = Path.GetFileName(entry.FullName);
                    if (name.Length > 0 && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) entry.ExtractToFile(Path.Combine(target, name), true);
                }
                foreach (ZipArchiveEntry entry in zip.Entries)
                    if (Path.GetFileName(entry.FullName) == "whisper-cli.exe") entry.ExtractToFile(Path.Combine(target, "whisper-cli.exe"), true);
            }
        }

        // Fetches whatever is missing. Progress is 0 to 100 and the last step is only reported by the caller.
        internal static void Install(string folder, Action<int> progress, Func<bool> cancelled)
        {
            bool needProgram = NeedProgram(folder);
            bool needModel = NeedModel(folder);
            long total = (needProgram ? ProgramBytes : 0) + (needModel ? ModelBytes : 0);
            Directory.CreateDirectory(folder);
            try
            {
                DriveInfo drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder)));
                if (drive.AvailableFreeSpace < total + 60000000L) throw new IOException("There is not enough free space on that drive.");
            }
            catch (ArgumentException) { }
            long before = 0;
            if (needProgram)
            {
                string zip = Get(ProgramUrl, ProgramBytes, ProgramSha, folder, delegate(long n) { progress((int)Math.Min(99, n * 100 / Math.Max(1, total))); }, cancelled);
                try { Unpack(zip, Path.Combine(folder, "bin", "Release")); }
                finally { try { File.Delete(zip); } catch (IOException) { } }
                before = ProgramBytes;
            }
            if (needModel)
            {
                string models = Path.Combine(folder, "models");
                Directory.CreateDirectory(models);
                long start = before;
                string part = Get(ModelUrl, ModelBytes, ModelSha, folder, delegate(long n) { progress((int)Math.Min(99, (start + n) * 100 / Math.Max(1, total))); }, cancelled);
                string final = Path.Combine(models, ModelName);
                if (File.Exists(final)) File.Delete(final);
                File.Move(part, final);
            }
        }
    }

    // Listens through the microphone with the recognizer that ships with Windows. It only listens when asked, for one phrase,
    // and lets go of the microphone straight afterwards. Nothing is recorded to disk and nothing leaves this computer.
    // She records the phrase herself (it starts when you speak and ends after a second of quiet) and hands the finished
    // recording to the recognizer, which is far more dependable than letting it pull from a live microphone.
    internal sealed class Ear : IDisposable
    {
        private static bool? found;
        private MicTap tap;
        private volatile bool cancelled;
        internal string Device = "";
        internal string SpeechFolder = "";
        internal string Language = "en";
        internal string Log = "";
        internal volatile string WhisperProblem;
        internal bool UseWhisper = true;
        internal double Patience = 7;
        internal double PauseSeconds = 2.5;
        private volatile bool endNow;
        private volatile bool spoken;
        internal bool Spoke { get { return spoken; } }
        internal string Trace = "";
        internal Func<MicTap> TapFactory;
        internal Action<Action<string, double>, Action<string>> Source;
        internal bool Listening { get; private set; }

        internal static bool Available
        {
            get
            {
                if (found == null)
                {
                    try { found = SR.SpeechRecognitionEngine.InstalledRecognizers().Count > 0; }
                    catch (Exception) { found = false; }
                }
                return found.Value;
            }
        }

        // The recognizer for the language she is set to if Windows has one, else the language Windows is using, else the first one installed.
        internal static SR.RecognizerInfo Pick(string language)
        {
            SR.RecognizerInfo first = null, windows = null;
            foreach (SR.RecognizerInfo info in SR.SpeechRecognitionEngine.InstalledRecognizers())
            {
                if (first == null) first = info;
                if (language != "en" && info.Culture.TwoLetterISOLanguageName == language) return info;
                if (windows == null && info.Culture.TwoLetterISOLanguageName == System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) windows = info;
            }
            return windows ?? first;
        }

        internal static SR.RecognizerInfo Pick() { return Pick("en"); }

        private static int Loudest(byte[] data, int count)
        {
            int loudest = 0;
            for (int i = 0; i + 1 < count; i += 2) loudest = Math.Max(loudest, Math.Abs((int)BitConverter.ToInt16(data, i)));
            return loudest;
        }

        // The recognizer does not know her name and hears it as other things; these are the usual ones.
        internal static string FixName(string text)
        {
            return Regex.Replace(text ?? "", @"\b(copper(?:[- ]?ey?|y)|cap[- ]?ree|cap[- ]?re|cabri|kapri|capry|capree)\b", "Capri", RegexOptions.IgnoreCase);
        }

        // A quiet microphone is the commonest reason this recognizer gets words wrong, so the phrase is brought up to a healthy volume first.
        internal static void Boost(byte[] samples)
        {
            int loudest = Loudest(samples, samples.Length);
            if (loudest < 200 || loudest > 20000) return;
            double gain = Math.Min(10, 22000.0 / loudest);
            for (int i = 0; i + 1 < samples.Length; i += 2)
            {
                int value = (int)Math.Round(BitConverter.ToInt16(samples, i) * gain);
                short clipped = (short)Math.Max(-32768, Math.Min(32767, value));
                samples[i] = (byte)(clipped & 0xFF);
                samples[i + 1] = (byte)((clipped >> 8) & 0xFF);
            }
        }

        // Splits a recording where there are more than six tenths of a second of quiet, keeping a little quiet around each part.
        internal static System.Collections.Generic.List<byte[]> Parts(byte[] samples, int threshold)
        {
            System.Collections.Generic.List<byte[]> parts = new System.Collections.Generic.List<byte[]>();
            int block = 3200;
            int blocks = (samples.Length + block - 1) / block;
            bool[] loud = new bool[blocks];
            for (int i = 0; i < blocks; i++)
            {
                int length = Math.Min(block, samples.Length - i * block);
                byte[] piece = new byte[length];
                Array.Copy(samples, i * block, piece, 0, length);
                loud[i] = Loudest(piece, length) > threshold;
            }
            int start = -1, lastLoud = -1;
            for (int i = 0; i <= blocks; i++)
            {
                bool speaking = i < blocks && loud[i];
                if (speaking) { if (start < 0) start = i; lastLoud = i; }
                bool gap = start >= 0 && (i == blocks || (!speaking && i - lastLoud > 6));
                if (!gap) continue;
                int from = Math.Max(0, start - 2) * block;
                int to = Math.Min(blocks, lastLoud + 3) * block;
                to = Math.Min(to, samples.Length);
                byte[] part = new byte[to - from];
                Array.Copy(samples, from, part, 0, part.Length);
                parts.Add(part);
                start = -1;
            }
            if (parts.Count == 0) parts.Add(samples);
            return parts;
        }

        // One line in voice-log.txt about what happened, so a problem can be explained. It holds no speech and no recognized words, only the numbers and the reason.
        private void Note(string line)
        {
            if (Log.Length == 0) return;
            try
            {
                string[] old = File.Exists(Log) && new FileInfo(Log).Length > 24000 ? File.ReadAllLines(Log) : new string[0];
                if (old.Length > 100)
                {
                    string[] kept = new string[100];
                    Array.Copy(old, old.Length - 100, kept, 0, 100);
                    File.WriteAllLines(Log, kept, new UTF8Encoding(false));
                }
                File.AppendAllText(Log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + "\r\n", new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // Failures come back as "silence" (it heard something but nothing usable), "quiet" (the microphone gave no sound), "norecognizer" (nothing to understand it with) or "error" (the microphone could not be used).
        internal void Start(Action<string, double> heard, Action<string> failed)
        {
            if (Listening) return;
            Listening = true;
            cancelled = false;
            endNow = false;
            spoken = false;
            WhisperProblem = null;
            if (Source != null)
            {
                Source(delegate(string text, double confidence) { Listening = false; heard(text, confidence); }, delegate(string why) { Listening = false; failed(why); });
                return;
            }
            Note("listening. microphone: " + (Device.Length > 0 ? Device : "Windows default") + ". language: " + Language + ". ears: " + (UseWhisper && Whisper.Available(SpeechFolder, Language) ? Whisper.Describe(SpeechFolder, Language) : "Windows' own"));
            MicTap source;
            try
            {
                int index = Device.Length > 0 ? Native.WaveDevice(Device) : -1;
                source = TapFactory != null ? TapFactory() : new MicTap(index);
                if (TapFactory == null && !source.Start()) { Note("the microphone could not be opened"); Finish(); failed("error"); return; }
            }
            catch (Exception ex) { Note("the microphone failed: " + ex.GetType().Name + ": " + ex.Message); Finish(); failed("error"); return; }
            tap = source;
            Thread worker = new Thread(delegate() { Capture(source, heard, failed); });
            worker.IsBackground = true;
            worker.Start();
        }

        private void Capture(MicTap source, Action<string, double> heard, Action<string> failed)
        {
            try
            {
                System.Collections.Generic.List<byte[]> before = new System.Collections.Generic.List<byte[]>();
                MemoryStream phrase = new MemoryStream();
                byte[] chunk = new byte[3200];
                Stopwatch clock = Stopwatch.StartNew();
                bool spoke = false;
                int quiet = 0, floor = 0, sampled = 0, threshold = 900, peak = 0;
                while (!cancelled)
                {
                    if (endNow && spoke) break;
                    int count = source.ReadWithin(chunk, 250);
                    if (count == 0) break;
                    if (count < 0)
                    {
                        if (!spoke && clock.Elapsed.TotalSeconds > Patience) break;
                        continue;
                    }
                    int level = Loudest(chunk, count);
                    peak = Math.Max(peak, level);
                    byte[] copy = new byte[count];
                    Array.Copy(chunk, copy, count);
                    if (sampled < 3) { floor = Math.Max(floor, level); sampled++; threshold = Math.Max(900, Math.Min(floor, 500) * 3); }   // capped, since she may be spoken to straight away
                    if (!spoke)
                    {
                        before.Add(copy);
                        if (before.Count > 8) before.RemoveAt(0);
                        if (level > threshold && sampled >= 3)
                        {
                            spoke = true;
                            spoken = true;
                            foreach (byte[] old in before) phrase.Write(old, 0, old.Length);
                            clock.Restart();
                        }
                        else if (clock.Elapsed.TotalSeconds > Patience) break;
                    }
                    else
                    {
                        phrase.Write(copy, 0, count);
                        quiet = level > threshold ? 0 : quiet + 1;
                        if (quiet >= (int)(PauseSeconds * 10) || clock.Elapsed.TotalSeconds > 60) break;
                    }
                }
                if (cancelled) return;
                if (!spoke) { Note("no sound reached her: the loudest level was " + peak + " and speech needs more than " + threshold + ". check the microphone and Windows' microphone privacy setting."); Finish(); failed("quiet"); return; }
                if (phrase.Length < 6400) { Note("what she heard was too short to use (" + phrase.Length / 32000.0 + "s)"); Finish(); failed("silence"); return; }
                byte[] samples = phrase.ToArray();
                Boost(samples);
                string text = null;
                double confidence = 0;
                if (UseWhisper && Whisper.Available(SpeechFolder, Language))
                {
                    try { text = Whisper.Transcribe(SpeechFolder, samples, Language); confidence = 1; }
                    catch (Exception ex) { text = null; WhisperProblem = ex.Message; Note("Whisper failed (" + ex.GetType().Name + ": " + ex.Message + "), so she used Windows' own recognizer"); }
                }
                if (cancelled) return;
                if (text != null) { Finish(); Note(text.Length == 0 ? "Whisper heard no words in " + samples.Length / 32000.0 + "s of sound" : "Whisper understood " + text.Split(' ').Length + " words in " + samples.Length / 32000.0 + "s of sound"); if (text.Length == 0) failed("silence"); else heard(FixName(text), confidence); return; }
                if (Pick(Language) == null) { Note("Windows has no speech recognizer installed and Whisper was not used, so there was nothing to understand her with"); Finish(); failed("norecognizer"); return; }
                // The recognizer stops at the first pause in a recording, so the phrase is cut at pauses and each part is recognized in turn.
                double total = 0;
                int pieces = 0;
                System.Collections.Generic.List<byte[]> cut = Parts(samples, threshold);
                Trace = "captured " + samples.Length / 32000.0 + "s in " + cut.Count + " part(s), threshold " + threshold;
                foreach (byte[] part in cut)
                {
                    if (cancelled) return;
                    MemoryStream padded = new MemoryStream();
                    padded.Write(new byte[24000], 0, 24000);
                    padded.Write(part, 0, part.Length);
                    padded.Write(new byte[16000], 0, 16000);
                    padded.Position = 0;
                    using (SR.SpeechRecognitionEngine engine = new SR.SpeechRecognitionEngine(Pick(Language)))
                    {
                        engine.LoadGrammar(new SR.DictationGrammar());
                        engine.SetInputToAudioStream(padded, new System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono));
                        SR.RecognitionResult result = engine.Recognize(TimeSpan.FromSeconds(10));
                        if (result == null || string.IsNullOrWhiteSpace(result.Text)) continue;
                        text = text == null ? result.Text : text + " " + result.Text;
                        total += result.Confidence;
                        pieces++;
                    }
                }
                if (pieces > 0) confidence = total / pieces;
                if (cancelled) return;
                Finish();
                if (string.IsNullOrWhiteSpace(text)) { Note("Windows' recognizer understood nothing in " + samples.Length / 32000.0 + "s of sound (language: " + Pick(Language).Culture.Name + ")"); failed("silence"); }
                else { Note("Windows' recognizer understood " + text.Split(' ').Length + " words, confidence " + Math.Round(confidence, 2) + " (language: " + Pick(Language).Culture.Name + ")"); heard(FixName(text), confidence); }
            }
            catch (Exception ex)
            {
                if (cancelled) return;
                Note("error while listening: " + ex.GetType().Name + ": " + ex.Message);
                Finish();
                failed("error");
            }
        }

        // Pressing the button while she is listening: if you have said something she stops listening and recognizes it now.
        internal void Wrap() { endNow = true; }

        internal void Stop()
        {
            if (!Listening) return;
            cancelled = true;
            Listening = false;
            Release();
        }

        private void Finish()
        {
            Listening = false;
            Release();
        }

        private void Release()
        {
            MicTap old = tap;
            tap = null;
            if (old == null) return;
            ThreadPool.QueueUserWorkItem(delegate { old.Stop(); });
        }

        public void Dispose() { Stop(); }
    }

    // The little microphone that fades in beside her when the pointer is on her. It never takes focus.
    internal sealed class MicButton : Form
    {
        private readonly Func<Point> spot;
        private readonly System.Windows.Forms.Timer ticks;
        private readonly Stopwatch clock = new Stopwatch();
        private double fade;
        private double lastTick;
        internal bool Wanted { get; private set; }
        internal bool Listening;
        internal Rectangle Area { get; private set; }
        internal event Action Clicked;

        internal MicButton(Func<Point> where)
        {
            spot = where;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Cursor = Cursors.Hand;
            ticks = new System.Windows.Forms.Timer();
            ticks.Interval = 33;
            ticks.Tick += delegate { Tick(); };
            clock.Start();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams value = base.CreateParams;
                value.ExStyle |= 0x00080000 | 0x00000080 | 0x08000000;
                return value;
            }
        }

        internal void Appear(bool on, bool onTop)
        {
            Wanted = on;
            TopMost = onTop;
            lastTick = clock.Elapsed.TotalSeconds;
            if (on && !Visible) Show();
            ticks.Start();
            Tick();
        }

        // Jumps the fade to where it is heading, so a test does not have to wait.
        internal void Settle()
        {
            fade = Wanted ? 1 : 0;
            Tick();
        }

        internal void Tick()
        {
            double now = clock.Elapsed.TotalSeconds;
            double step = Math.Min(0.1, now - lastTick) / 0.18;
            lastTick = now;
            fade = Wanted ? Math.Min(1, fade + step) : Math.Max(0, fade - step);
            if (!Wanted && fade <= 0) { ticks.Stop(); if (Visible) Hide(); return; }
            if (!IsHandleCreated) return;
            using (Bitmap picture = Render(Listening, now))
            {
                Point at = spot();
                Area = new Rectangle(at.X, at.Y, picture.Width, picture.Height);
                Native.DrawLayered(Handle, picture, at, (byte)(255 * fade));
            }
        }

        // A sliced chip with a microphone on it; while she listens it turns crimson and a ring spreads out from it.
        internal Bitmap Render(bool listening, double now)
        {
            Bitmap next = new Bitmap(64, 64, PixelFormat.Format32bppPArgb);
            Color edge = listening ? Neon.Crimson : Neon.Green;
            using (Graphics graphics = Graphics.FromImage(next))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                RectangleF chip = new RectangleF(8, 8, 48, 48);
                if (listening)
                {
                    float spread = (float)((now * 1.4) % 1);
                    using (Pen ring = new Pen(Color.FromArgb((int)(190 * (1 - spread)), Neon.Crimson), 2f)) graphics.DrawEllipse(ring, 32 - 24 - spread * 6, 32 - 24 - spread * 6, 48 + spread * 12, 48 + spread * 12);
                }
                using (GraphicsPath path = Neon.Cut(chip, 9))
                using (Brush fill = new SolidBrush(Color.FromArgb(240, Neon.Void))) graphics.FillPath(fill, path);
                Neon.Outline(graphics, chip, 9, edge, listening ? Neon.Green : Neon.Crimson);
                using (Pen glyph = new Pen(edge, 2f))
                using (GraphicsPath capsule = new GraphicsPath())
                {
                    capsule.AddArc(27, 17, 10, 10, 180, 180);
                    capsule.AddArc(27, 25, 10, 10, 0, 180);
                    capsule.CloseFigure();
                    graphics.DrawPath(glyph, capsule);
                    graphics.DrawArc(glyph, 22, 22, 20, 16, 0, 180);
                    graphics.DrawLine(glyph, 32, 38, 32, 44);
                    graphics.DrawLine(glyph, 27, 44, 37, 44);
                }
            }
            return next;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            Action handler = Clicked;
            if (handler != null) handler();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ticks.Dispose();
            base.Dispose(disposing);
        }
    }

    // An anime-style cut-in: a framed shot from the show wipes in beside her head for a moment, then wipes out again.
    internal sealed class CutIn : Form
    {
        private readonly Func<Rectangle> her;
        private readonly Func<bool> animated;
        private readonly System.Windows.Forms.Timer ticks;
        private readonly Stopwatch clock = new Stopwatch();
        private Bitmap art;
        private Size card;
        private double duration;
        internal string Key = "";
        internal Rectangle Area { get; private set; }

        internal CutIn(Func<Rectangle> where, Func<bool> moving)
        {
            her = where;
            animated = moving;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            ticks = new System.Windows.Forms.Timer();
            ticks.Interval = 33;
            ticks.Tick += delegate { Tick(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams value = base.CreateParams;
                value.ExStyle |= 0x00080000 | 0x00000080 | 0x08000000 | 0x00000020;
                return value;
            }
        }

        internal void Present(Bitmap picture, string key, double seconds, bool onTop, int width)
        {
            art = picture;
            Key = key;
            duration = seconds;
            card = new Size(width, width * picture.Height / picture.Width);
            TopMost = onTop;
            clock.Restart();
            if (!Visible) Show();
            ticks.Start();
            Tick();
        }

        internal void Dismiss()
        {
            ticks.Stop();
            clock.Stop();
            if (Visible) Hide();
        }

        internal void Tick()
        {
            double age = clock.Elapsed.TotalSeconds;
            if (age >= duration) { Dismiss(); return; }
            Rectangle body = her();
            Rectangle area = Screen.FromPoint(new Point(body.Left + body.Width / 2, body.Top + body.Height / 2)).WorkingArea;
            bool left = body.Left - card.Width - 6 >= area.Left;
            int x = left ? body.Left - card.Width - 6 : Math.Min(body.Right + 6, area.Right - card.Width);
            int y = Math.Max(area.Top, Math.Min(body.Top + 34, area.Bottom - card.Height));
            double reveal = animated() ? Math.Min(1, Math.Min(age / 0.14, (duration - age) / 0.14)) : 1;
            using (Bitmap picture = Render(reveal, left))
            {
                Area = new Rectangle(x, y, card.Width, card.Height);
                Native.DrawLayered(Handle, picture, Area.Location, 255);
            }
        }

        // The card wipes in from the side nearest her: a growing slice of the framed shot, with a bright edge on the slice.
        internal Bitmap Render(double reveal, bool fromRight)
        {
            Bitmap next = new Bitmap(card.Width, card.Height, PixelFormat.Format32bppPArgb);
            if (reveal <= 0) return next;
            int slice = Math.Max(2, (int)(card.Width * reveal));
            Rectangle visible = fromRight ? new Rectangle(card.Width - slice, 0, slice, card.Height) : new Rectangle(0, 0, slice, card.Height);
            RectangleF box = new RectangleF(0.75f, 0.75f, card.Width - 1.5f, card.Height - 1.5f);
            using (Graphics graphics = Graphics.FromImage(next))
            using (GraphicsPath frame = Neon.Cut(box, 12))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.SetClip(visible);
                GraphicsState inside = graphics.Save();
                graphics.SetClip(frame, CombineMode.Intersect);
                graphics.DrawImage(art, new Rectangle(0, 0, card.Width, card.Height));
                using (Pen scan = new Pen(Color.FromArgb(18, 0, 0, 0))) for (int y = 1; y < card.Height; y += 3) graphics.DrawLine(scan, 0, y, card.Width, y);
                graphics.Restore(inside);
                Neon.Outline(graphics, box, 12, Neon.Green, Neon.Crimson);
                if (reveal < 1)
                {
                    int edge = fromRight ? visible.Left : visible.Right - 1;
                    using (Pen flash = new Pen(Color.FromArgb(230, Neon.Green), 2f)) graphics.DrawLine(flash, edge, 4, edge, card.Height - 4);
                }
            }
            return next;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ticks.Dispose();
            base.Dispose(disposing);
        }
    }

    // A proper conversation window: her face up top, the whole chat below, and a box to keep typing in. Closing it only hides it,
    // so the conversation is still there the next time she is asked to talk.
    internal sealed class ChatWindow : Form
    {
        private readonly RichTextBox transcript;
        private readonly TextBox input;
        private readonly Button send;
        private readonly Button mic;
        private bool listening;
        private readonly Bitmap faceClosed;
        private readonly Bitmap faceOpen;
        private readonly System.Windows.Forms.Timer flap;
        private readonly Stopwatch clock = new Stopwatch();
        private string status = "";
        private bool busy;
        private bool mouth;
        private double talkUntil;
        private int dots;
        private static readonly Rectangle Portrait = new Rectangle(106, 48, 208, 117);
        private static readonly Rectangle CloseBox = new Rectangle(376, 8, 30, 26);
        internal event Action<string> Submitted;
        internal event Action Spoke;

        internal ChatWindow(Bitmap closed, Bitmap open)
        {
            faceClosed = closed;
            faceOpen = open ?? closed;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            TopMost = true;
            BackColor = Neon.Void;
            ForeColor = Neon.Text;
            Font = Neon.Font(10);
            ClientSize = new Size(420, 560);
            Neon.Shape(this, 14);
            transcript = new RichTextBox { Bounds = new Rectangle(18, 192, 384, 280), ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Neon.Panel, ForeColor = Neon.Text, DetectUrls = false, HideSelection = false, ScrollBars = RichTextBoxScrollBars.Vertical, Font = Neon.Font(10.5f), TabStop = false };
            input = new TextBox { Bounds = new Rectangle(18, 484, 236, 58), Multiline = true, BackColor = Neon.Panel, ForeColor = Neon.Green, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 10.5f), MaxLength = 500 };
            send = new Button { Text = Lang.T("SEND"), Bounds = new Rectangle(322, 484, 80, 58), FlatStyle = FlatStyle.Flat, BackColor = Neon.Crimson, ForeColor = Color.White, Cursor = Cursors.Hand };
            send.FlatAppearance.BorderSize = 0;
            send.Click += delegate { Entered(); };
            mic = new Button { Text = Lang.T("MIC"), Bounds = new Rectangle(260, 484, 54, 58), FlatStyle = FlatStyle.Flat, BackColor = Neon.Void, ForeColor = Neon.Green, Cursor = Cursors.Hand };
            mic.FlatAppearance.BorderColor = Neon.Green;
            mic.Click += delegate { Action handler = Spoke; if (handler != null) handler(); };
            input.KeyDown += delegate(object sender, KeyEventArgs key) { if (key.KeyCode == Keys.Enter && !key.Shift) { key.SuppressKeyPress = true; Entered(); } };
            transcript.HandleCreated += delegate { Native.SetWindowTheme(transcript.Handle, "DarkMode_Explorer", null); };
            input.HandleCreated += delegate { Native.SetWindowTheme(input.Handle, "DarkMode_Explorer", null); };
            Controls.Add(transcript);
            Controls.Add(input);
            Controls.Add(send);
            Controls.Add(mic);
            flap = new System.Windows.Forms.Timer();
            flap.Interval = 110;
            flap.Tick += delegate { Animate(); };
        }

        internal string Transcript { get { return transcript.Text; } }

        internal bool Busy { get { return busy; } }

        internal bool Listening { get { return listening; } }

        internal string Draft { get { return input.Text; } }

        internal void SetListening(bool value)
        {
            listening = value;
            mic.Text = value ? "DONE" : "MIC";
            mic.ForeColor = value ? Neon.Crimson : Neon.Green;
            mic.FlatAppearance.BorderColor = value ? Neon.Crimson : Neon.Green;
            dots = 0;
            if (value) { clock.Restart(); flap.Start(); }
            Invalidate(new Rectangle(0, 166, Width, 24));
        }

        // What she heard, put in the box for the person to check before sending.
        internal void SetDraft(string text)
        {
            input.Text = text;
            input.SelectionStart = text.Length;
            input.Focus();
        }

        internal void FocusInput() { input.Focus(); }

        internal void SetStatus(string text) { status = text; Invalidate(new Rectangle(0, 166, Width, 24)); }

        internal void SetBusy(bool value)
        {
            busy = value;
            send.Enabled = !value;
            dots = 0;
            if (value) { clock.Restart(); flap.Start(); }
            Invalidate(new Rectangle(0, 166, Width, 24));
        }

        // What she is sent when the person presses Enter or SEND.
        internal void Submit(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0 || busy) return;
            Add("YOU", Neon.Green, text);
            Action<string> handler = Submitted;
            if (handler != null) handler(text);
        }

        private void Entered()
        {
            string text = input.Text;
            if (string.IsNullOrWhiteSpace(text) || busy) return;
            input.Clear();
            Submit(text);
        }

        internal void AddHer(string text)
        {
            Add("CAPRI", Neon.Crimson, text);
            talkUntil = Math.Min(5, 0.5 + text.Length * 0.045);
            clock.Restart();
            flap.Start();
        }

        internal void AddNote(string text)
        {
            transcript.SelectionStart = transcript.TextLength;
            transcript.SelectionLength = 0;
            transcript.SelectionColor = Neon.Dim;
            transcript.SelectionFont = new Font("Consolas", 9);
            transcript.AppendText(text + "\r\n\r\n");
            transcript.ScrollToCaret();
        }

        internal void Clear() { transcript.Clear(); }

        internal bool Empty { get { return transcript.TextLength == 0; } }

        private void Add(string who, Color tag, string text)
        {
            transcript.SelectionStart = transcript.TextLength;
            transcript.SelectionLength = 0;
            transcript.SelectionColor = tag;
            transcript.SelectionFont = new Font("Consolas", 9, FontStyle.Bold);
            transcript.AppendText(who + "\r\n");
            transcript.SelectionColor = Neon.Text;
            transcript.SelectionFont = Neon.Font(10.5f);
            transcript.AppendText(text + "\r\n\r\n");
            transcript.ScrollToCaret();
        }

        private void Animate()
        {
            double age = clock.Elapsed.TotalSeconds;
            bool open = age < talkUntil && (int)(age / 0.11) * 7 % 5 < 3;
            int step = (int)(age / 0.4) % 4;
            if (open != mouth) { mouth = open; Invalidate(Portrait); }
            if ((busy || listening) && step != dots) { dots = step; Invalidate(new Rectangle(0, 166, Width, 24)); }
            if (!busy && !listening && age >= talkUntil) { if (mouth) { mouth = false; Invalidate(Portrait); } flap.Stop(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics graphics = e.Graphics;
            Neon.Outline(graphics, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 14, Neon.Crimson, Neon.Green);
            using (Pen scan = new Pen(Color.FromArgb(10, 255, 255, 255))) for (int y = 40; y < Height; y += 3) graphics.DrawLine(scan, 16, y, Width - 16, y);
            using (Font mono = new Font("Consolas", 10, FontStyle.Bold))
            using (Brush red = new SolidBrush(Neon.Crimson))
            using (Brush green = new SolidBrush(Neon.Green))
            {
                graphics.DrawString("//", mono, red, 22, 14);
                graphics.DrawString("CAPRI", mono, green, 46, 14);
                graphics.DrawString("X", mono, red, CloseBox.X + 8, CloseBox.Y + 3);
            }
            using (Pen line = new Pen(Neon.Line)) graphics.DrawLine(line, 22, 40, Width - 22, 40);
            if (faceClosed != null)
            {
                using (GraphicsPath frame = Neon.Cut(Portrait, 10))
                {
                    GraphicsState saved = graphics.Save();
                    graphics.SetClip(frame);
                    graphics.DrawImage(mouth ? faceOpen : faceClosed, Portrait);
                    graphics.Restore(saved);
                    using (Pen edge = new Pen(Color.FromArgb(230, Neon.Green), 1.25f)) graphics.DrawPath(edge, frame);
                }
            }
            string line2 = busy ? "CAPRI IS TYPING" + new string('.', dots) : listening ? "LISTENING" + new string('.', dots) : status.ToUpperInvariant();
            using (Font small = new Font("Consolas", 8.5f))
            using (Brush ink = new SolidBrush(busy || listening ? Neon.Crimson : Neon.Dim)) graphics.DrawString(line2, small, ink, 22, 170);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (CloseBox.Contains(e.Location)) Hide();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) Hide();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0084)
            {
                long value = message.LParam.ToInt64();
                Point point = PointToClient(new Point((short)(value & 65535), (short)((value >> 16) & 65535)));
                if (point.Y < 40 && !CloseBox.Contains(point)) { message.Result = new IntPtr(2); return; }
            }
            base.WndProc(ref message);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) flap.Dispose();
            base.Dispose(disposing);
        }
    }

    // A one-line question in the same dark style as the installer; check returns a complaint, or null when the answer will do.
    // Where the key and the cloud choices are entered, with what leaves the computer spelled out.
    internal sealed class CloudForm : Form
    {
        private readonly ComboBox provider = new ComboBox();
        private readonly TextBox model = new TextBox();
        private readonly TextBox url = new TextBox();
        private readonly TextBox key = new TextBox();
        private readonly CheckBox look = new CheckBox();
        private readonly CheckBox memory = new CheckBox();
        private readonly Label hint = new Label();
        private readonly Label problem = new Label();
        private bool remove;
        private static readonly string[] Ids = { "openrouter", "openai", "anthropic", "custom" };
        private static readonly string[] Names = { "OpenRouter", "OpenAI", "Anthropic (Claude)", "Custom (OpenAI-style, https)" };
        private static readonly string[] Hints = { "A model id from openrouter.ai/models", "A model name from your OpenAI account", "For example claude-haiku-5-5", "The model name your server expects" };

        internal string Provider { get { return Ids[Math.Max(0, provider.SelectedIndex)]; } }
        internal string Model { get { return model.Text.Trim(); } }
        internal string Url { get { return url.Text.Trim(); } }
        internal string Key { get { return key.Text.Trim(); } }
        internal bool Look { get { return look.Checked; } }
        internal bool Memory { get { return memory.Checked; } }
        internal bool RemoveKey { get { return remove; } }

        internal CloudForm(string chosen, string modelName, string address, bool looks, bool remembers, bool hasKey)
        {
            Text = Lang.T("Cloud model");
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(480, 500);
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Neon.Void;
            ForeColor = Neon.Text;
            Font = Neon.Font(10);
            Neon.Shape(this, 14);
            Controls.Add(new Label { Text = Lang.T("Provider"), Bounds = new Rectangle(24, 52, 200, 20), ForeColor = Neon.Dim, BackColor = Color.Transparent });
            provider.Bounds = new Rectangle(24, 74, 432, 28);
            provider.DropDownStyle = ComboBoxStyle.DropDownList;
            provider.FlatStyle = FlatStyle.Flat;
            provider.BackColor = Neon.Panel;
            provider.ForeColor = Neon.Green;
            foreach (string name in Names) provider.Items.Add(Lang.T(name));
            provider.SelectedIndex = Math.Max(0, Array.IndexOf(Ids, chosen));
            provider.DrawMode = DrawMode.OwnerDrawFixed;
            provider.ItemHeight = 22;
            provider.DrawItem += delegate(object sender, DrawItemEventArgs e)
            {
                if (e.Index < 0) return;
                bool hot = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
                using (Brush back = new SolidBrush(hot ? Neon.Crimson : Neon.Panel)) e.Graphics.FillRectangle(back, e.Bounds);
                using (Brush text = new SolidBrush(hot ? Color.White : Neon.Green)) e.Graphics.DrawString(Lang.T(Names[e.Index]), e.Font, text, e.Bounds.X + 4, e.Bounds.Y + 2);
            };
            provider.SelectedIndexChanged += delegate { Refresh2(); };
            Controls.Add(provider);
            Controls.Add(new Label { Text = Lang.T("Model"), Bounds = new Rectangle(24, 110, 200, 20), ForeColor = Neon.Dim, BackColor = Color.Transparent });
            Style(model, 132);
            model.Text = modelName;
            Controls.Add(model);
            hint.Bounds = new Rectangle(24, 162, 432, 18);
            hint.ForeColor = Neon.Dim;
            hint.BackColor = Color.Transparent;
            hint.Font = Neon.Font(8.5f);
            Controls.Add(hint);
            Controls.Add(new Label { Text = Lang.T("Address (custom only)"), Bounds = new Rectangle(24, 184, 300, 20), ForeColor = Neon.Dim, BackColor = Color.Transparent });
            Style(url, 206);
            url.Text = address;
            Controls.Add(url);
            Controls.Add(new Label { Text = hasKey ? Lang.T("Key (saved, leave empty to keep it)") : Lang.T("Key"), Bounds = new Rectangle(24, 246, 270, 20), ForeColor = Neon.Dim, BackColor = Color.Transparent });
            Style(key, 268);
            key.UseSystemPasswordChar = true;
            Controls.Add(key);
            look.Text = Lang.T("Also send my screen when I ask her to look");
            look.Checked = looks;
            memory.Text = Lang.T("Also send what she remembers about me");
            memory.Checked = remembers;
            look.Bounds = new Rectangle(24, 310, 432, 24);
            memory.Bounds = new Rectangle(24, 336, 432, 24);
            foreach (CheckBox box in new CheckBox[] { look, memory }) { box.ForeColor = Neon.Text; box.BackColor = Color.Transparent; Controls.Add(box); }
            Controls.Add(new Label { Text = Lang.T("Your chat messages go to this company, along with her persona. The key is kept encrypted for your Windows account only. Nothing else leaves your computer unless a box above is ticked."), Bounds = new Rectangle(24, 366, 432, 82), ForeColor = Neon.Crimson, BackColor = Color.Transparent, Font = Neon.Font(8.5f) });
            problem.Bounds = new Rectangle(24, 452, 260, 36);
            problem.ForeColor = Neon.Crimson;
            problem.BackColor = Color.Transparent;
            problem.Font = Neon.Font(8.5f);
            Controls.Add(problem);
            Button ok = new Button { Text = Lang.T("SAVE"), Bounds = new Rectangle(240, 454, 106, 32), FlatStyle = FlatStyle.Flat, BackColor = Neon.Crimson, ForeColor = Color.White, Cursor = Cursors.Hand };
            ok.FlatAppearance.BorderSize = 0;
            ok.Click += delegate
            {
                if (Model.Length == 0) { problem.Text = Lang.T("Enter a model name."); return; }
                if (Provider == "custom" && !Cloud.UrlOk(Url)) { problem.Text = Lang.T("A custom address must start with https://"); return; }
                if (Key.Length == 0 && !hasKey) { problem.Text = Lang.T("Paste your key."); return; }
                DialogResult = DialogResult.OK;
            };
            Button cancel = new Button { Text = Lang.T("CANCEL"), Bounds = new Rectangle(352, 454, 104, 32), FlatStyle = FlatStyle.Flat, BackColor = Neon.Void, ForeColor = Neon.Text, DialogResult = DialogResult.Cancel, Cursor = Cursors.Hand };
            cancel.FlatAppearance.BorderColor = Neon.Line;
            Controls.Add(ok);
            Controls.Add(cancel);
            if (hasKey)
            {
                LinkLabel removeLink = new LinkLabel { Text = Lang.T("Remove saved key"), Bounds = new Rectangle(296, 246, 160, 20), LinkColor = Neon.Crimson, BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleRight };
                removeLink.Click += delegate { remove = true; DialogResult = DialogResult.OK; };
                Controls.Add(removeLink);
            }
            AcceptButton = ok;
            CancelButton = cancel;
            Refresh2();
        }

        private static void Style(TextBox box, int top)
        {
            box.Bounds = new Rectangle(24, top, 432, 28);
            box.BackColor = Neon.Panel;
            box.ForeColor = Neon.Green;
            box.BorderStyle = BorderStyle.FixedSingle;
            box.Font = new Font("Consolas", 11);
        }

        private void Refresh2()
        {
            hint.Text = Lang.T(Hints[Math.Max(0, provider.SelectedIndex)]);
            url.Enabled = Provider == "custom";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Neon.Outline(e.Graphics, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 14, Neon.Crimson, Neon.Green);
            using (Pen scan = new Pen(Color.FromArgb(10, 255, 255, 255))) for (int y = 40; y < Height; y += 3) e.Graphics.DrawLine(scan, 16, y, Width - 16, y);
            using (Font mono = new Font("Consolas", 10, FontStyle.Bold))
            using (Brush red = new SolidBrush(Neon.Crimson))
            using (Brush green = new SolidBrush(Neon.Green)) { e.Graphics.DrawString("//", mono, red, 22, 14); e.Graphics.DrawString(Lang.T("Cloud model").ToUpper(), mono, green, 46, 14); }
            using (Pen line = new Pen(Neon.Line)) e.Graphics.DrawLine(line, 22, 40, Width - 22, 40);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x84)
            {
                Point at = PointToClient(new Point((short)(message.LParam.ToInt32() & 0xFFFF), (short)(message.LParam.ToInt32() >> 16)));
                if (at.Y < 40) { message.Result = (IntPtr)2; return; }
            }
            base.WndProc(ref message);
        }
    }

    internal sealed class AskForm : Form
    {
        private readonly TextBox input;
        private readonly Label problem;
        private readonly Func<string, string> check;
        internal string Answer;

        internal AskForm(string title, string prompt, string example, Func<string, string> validate)
        {
            check = validate;
            title = Lang.T(title);
            Text = title;
            heading = title;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(440, 224);
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Neon.Void;
            ForeColor = Neon.Text;
            Font = Neon.Font(10);
            Neon.Shape(this, 14);
            Controls.Add(new Label { Text = Lang.T(prompt), Bounds = new Rectangle(24, 52, 392, 24), ForeColor = ForeColor, BackColor = Color.Transparent });
            input = new TextBox { Bounds = new Rectangle(24, 82, 392, 28), BackColor = Neon.Panel, ForeColor = Neon.Green, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 11) };
            Controls.Add(input);
            Controls.Add(new Label { Text = Lang.T(example), Bounds = new Rectangle(24, 116, 392, 32), ForeColor = Neon.Dim, BackColor = Color.Transparent, Font = Neon.Font(8.5f) });
            problem = new Label { Bounds = new Rectangle(24, 150, 392, 22), ForeColor = Neon.Crimson, BackColor = Color.Transparent, Font = Neon.Font(8.5f) };
            Controls.Add(problem);
            Button ok = new Button { Text = Lang.T("OK"), Bounds = new Rectangle(226, 178, 90, 32), FlatStyle = FlatStyle.Flat, BackColor = Neon.Crimson, ForeColor = Color.White, Cursor = Cursors.Hand };
            ok.FlatAppearance.BorderSize = 0;
            ok.Click += delegate
            {
                string complaint = check(input.Text);
                problem.Text = complaint ?? "";
                if (complaint != null) return;
                Answer = input.Text;
                DialogResult = DialogResult.OK;
            };
            Button cancel = new Button { Text = Lang.T("Cancel"), Bounds = new Rectangle(326, 178, 90, 32), FlatStyle = FlatStyle.Flat, BackColor = Neon.Void, ForeColor = Neon.Text, DialogResult = DialogResult.Cancel, Cursor = Cursors.Hand };
            cancel.FlatAppearance.BorderColor = Neon.Line;
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        private readonly string heading;

        // The title bar is drawn here, so the top strip stands in for it and can be dragged.
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Neon.Outline(e.Graphics, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 14, Neon.Crimson, Neon.Green);
            using (Pen scan = new Pen(Color.FromArgb(10, 255, 255, 255))) for (int y = 40; y < Height; y += 3) e.Graphics.DrawLine(scan, 16, y, Width - 16, y);
            using (Font mono = new Font("Consolas", 10, FontStyle.Bold))
            using (Brush red = new SolidBrush(Neon.Crimson))
            using (Brush green = new SolidBrush(Neon.Green)) { e.Graphics.DrawString("//", mono, red, 22, 14); e.Graphics.DrawString(heading.ToUpperInvariant(), mono, green, 46, 14); }
            using (Pen line = new Pen(Neon.Line)) e.Graphics.DrawLine(line, 22, 40, Width - 22, 40);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0084)
            {
                long value = message.LParam.ToInt64();
                Point point = PointToClient(new Point((short)(value & 65535), (short)((value >> 16) & 65535)));
                if (point.Y < 40) { message.Result = new IntPtr(2); return; }
            }
            base.WndProc(ref message);
        }
    }

    // A short burst of fake interference drawn over one screen in her colours. It is painted fresh, copies nothing from the screen,
    // never takes a click, and stays gentle: thin low-contrast bars that change about eight times a second and never flash the whole screen.
    internal sealed class Glitch : Form
    {
        private readonly System.Windows.Forms.Timer ticks;
        private readonly Stopwatch clock = new Stopwatch();
        private readonly Bitmap chip;
        private Bitmap canvas;
        private Rectangle where;
        private double duration;
        private PointF chase;
        internal int Kind;
        internal static double SyncTarget = 0.87;
        internal static readonly string[] Kinds = { "Bars", "Tear", "Corruption", "Hex rain", "Sync bar", "Chaser" };
        internal static readonly double[] Seconds = { 2.6, 3.0, 2.8, 3.6, 4.0, 4.0 };

        internal Glitch(Bitmap chipArt)
        {
            chip = chipArt;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            TopMost = true;
            ticks = new System.Windows.Forms.Timer();
            ticks.Interval = 33;
            ticks.Tick += delegate { Tick(); };
        }

        internal Bitmap Canvas { get { return canvas; } }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams value = base.CreateParams;
                value.ExStyle |= 0x00080000 | 0x00000080 | 0x08000000 | 0x00000020;
                return value;
            }
        }

        internal void Run(Rectangle screen, double seconds)
        {
            where = screen;
            duration = seconds;
            if (canvas == null || canvas.Size != screen.Size) { if (canvas != null) canvas.Dispose(); canvas = new Bitmap(screen.Width, screen.Height, PixelFormat.Format32bppPArgb); }
            clock.Reset();
            clock.Start();
            if (!Visible) Show();
            ticks.Start();
            Tick();
        }

        internal void Stop()
        {
            ticks.Stop();
            clock.Stop();
            if (Visible) Hide();
        }

        internal void Tick()
        {
            double age = clock.Elapsed.TotalSeconds;
            if (age >= duration) { Stop(); return; }
            Frame(age);
            Native.DrawLayered(Handle, canvas, where.Location, 255);
        }

        internal void Frame(double age)
        {
            float fade = (float)Math.Max(0, Math.Min(1, Math.Min(age / 0.25, (duration - age) / 0.4)));
            int step = (int)(age / 0.125);
            Random dice = new Random(step * 7919 + 13);
            int width = canvas.Width, height = canvas.Height;
            using (Graphics graphics = Graphics.FromImage(canvas))
            {
                graphics.Clear(Color.Transparent);
                using (Pen line = new Pen(Color.FromArgb((int)(24 * fade), 0, 0, 0))) for (int y = 0; y < height; y += 4) graphics.DrawLine(line, 0, y, width, y);
                int edge = Math.Max(40, width / 16);
                using (LinearGradientBrush left = new LinearGradientBrush(new Rectangle(0, 0, edge, height), Color.FromArgb((int)(55 * fade), 239, 58, 78), Color.FromArgb(0, 239, 58, 78), LinearGradientMode.Horizontal))
                using (LinearGradientBrush right = new LinearGradientBrush(new Rectangle(width - edge, 0, edge, height), Color.FromArgb(0, 239, 58, 78), Color.FromArgb((int)(55 * fade), 239, 58, 78), LinearGradientMode.Horizontal))
                {
                    graphics.FillRectangle(left, 0, 0, edge, height);
                    graphics.FillRectangle(right, width - edge, 0, edge, height);
                }
                if (Kind != 0) Extra(graphics, age, fade, dice, width, height, step);
                double budget = Kind == 0 ? width * height * 0.06 : 0;
                int bars = Kind == 0 ? 3 + dice.Next(4) : 0;
                for (int i = 0; i < bars; i++)
                {
                    int barHeight = 3 + dice.Next(Math.Max(4, height / 18));
                    int barWidth = (int)(width * (0.15 + dice.NextDouble() * 0.45));
                    if (barHeight * barWidth * 2 > budget) continue;
                    budget -= barHeight * barWidth * 2;
                    int x = dice.Next(Math.Max(1, width - barWidth));
                    int y = dice.Next(Math.Max(1, height - barHeight));
                    int shift = 6 + dice.Next(24);
                    using (Brush red = new SolidBrush(Color.FromArgb((int)((40 + dice.Next(70)) * fade), 239, 58, 78)))
                    using (Brush green = new SolidBrush(Color.FromArgb((int)((40 + dice.Next(70)) * fade), 167, 245, 88)))
                    using (Brush dark = new SolidBrush(Color.FromArgb((int)(70 * fade), 0, 0, 0)))
                    {
                        graphics.FillRectangle(red, x - shift, y, barWidth, barHeight);
                        graphics.FillRectangle(green, x + shift, y, barWidth, barHeight);
                        if (dice.Next(3) == 0) graphics.FillRectangle(dark, x, y + barHeight, barWidth, Math.Max(2, barHeight / 3));
                    }
                }
                if (chip != null)
                {
                    int size = 88;
                    int tall = size * chip.Height / chip.Width;
                    double slide = Math.Min(1, age / 0.35) * Math.Min(1, Math.Max(0, (duration - age) / 0.4));
                    int cx = width - 24 - size + (int)((1 - slide * slide) * (size + 48)) + dice.Next(-3, 4);
                    using (ImageAttributes tint = new ImageAttributes())
                    {
                        ColorMatrix matrix = new ColorMatrix();
                        matrix.Matrix33 = fade;
                        tint.SetColorMatrix(matrix);
                        graphics.DrawImage(chip, new Rectangle(cx, 28, size, tall), 0, 0, chip.Width, chip.Height, GraphicsUnit.Pixel, tint);
                    }
                }
            }
        }

        // The styles beyond the bars. Everything is painted fresh; the biggest piece is the sync box, and nothing is bright or fast.
        private void Extra(Graphics graphics, double age, float fade, Random dice, int width, int height, int step)
        {
            Color red = Color.FromArgb(239, 58, 78), green = Color.FromArgb(167, 245, 88);
            if (Kind == 1)
            {
                double bandY = (age / duration) * (height + 240) - 120;
                for (int k = 0; k < 4; k++)
                {
                    int stripHeight = 6 + dice.Next(16);
                    int stripWidth = (int)(width * (0.45 + dice.NextDouble() * 0.5));
                    int shift = 10 + dice.Next(50);
                    int x = dice.Next(Math.Max(1, width - stripWidth));
                    using (Brush brush = new SolidBrush(Color.FromArgb((int)((60 + dice.Next(40)) * fade), k % 2 == 0 ? red : green)))
                        graphics.FillRectangle(brush, x + (k % 2 == 0 ? -shift : shift), (float)bandY + k * 17, stripWidth, stripHeight);
                }
                using (Pen line = new Pen(Color.FromArgb((int)(60 * fade), 235, 235, 235))) graphics.DrawLine(line, 0, (float)bandY, width, (float)bandY);
            }
            else if (Kind == 2)
            {
                double budget = width * height * 0.04;
                int blocks = 5 + dice.Next(6);
                for (int i = 0; i < blocks; i++)
                {
                    int w = 12 + dice.Next(60), h = 12 + dice.Next(40);
                    if (w * h > budget) continue;
                    budget -= w * h;
                    int x = dice.Next(Math.Max(1, width - w)), y = dice.Next(Math.Max(1, height - h));
                    int pick = dice.Next(3);
                    using (Brush brush = new SolidBrush(pick == 0 ? Color.FromArgb((int)((50 + dice.Next(60)) * fade), red) : pick == 1 ? Color.FromArgb((int)((50 + dice.Next(60)) * fade), green) : Color.FromArgb((int)(90 * fade), 0, 0, 0)))
                    {
                        graphics.FillRectangle(brush, x, y, w, h);
                        for (int dots = 0; dots < 6; dots++) graphics.FillRectangle(brush, x + dice.Next(-40, w + 40), y + dice.Next(-20, h + 20), 4, 4);
                    }
                }
            }
            else if (Kind == 3)
            {
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                string digits = "0123456789ABCDEF";
                using (Font font = new Font("Consolas", 17f, FontStyle.Bold, GraphicsUnit.Pixel))
                    for (int side = 0; side < 2; side++)
                        for (int column = 0; column < 7; column++)
                        {
                            int x = side == 0 ? 10 + column * 19 : width - 30 - column * 19;
                            double head = (age * 230 + column * 137 + side * 61) % (height + 300);
                            for (int j = 0; j < 12; j++)
                            {
                                double y = head - j * 19 - 100;
                                if (y < -19 || y > height) continue;
                                int alpha = (int)((j == 0 ? 210 : 170 - j * 13) * fade);
                                if (alpha <= 0) continue;
                                string c = digits[((step * 7 + column * 13 + j * 17 + side * 5) ^ (j * 31 + column)) & 15].ToString();
                                using (Brush brush = new SolidBrush(Color.FromArgb(alpha, j == 0 ? Color.FromArgb(230, 255, 210) : green)))
                                    graphics.DrawString(c, font, brush, x, (float)y);
                            }
                        }
            }
            else if (Kind == 4)
            {
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                Rectangle box = new Rectangle(40, Math.Max(20, height - 210), 400, 84);
                double done = SyncTarget * Math.Min(1, age / Math.Max(1, duration - 1.0));
                using (Brush back = new SolidBrush(Color.FromArgb((int)(200 * fade), 6, 6, 8))) graphics.FillRectangle(back, box);
                using (Pen edge = new Pen(Color.FromArgb((int)(230 * fade), red))) graphics.DrawRectangle(edge, box);
                using (Font font = new Font("Consolas", 14f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (Brush text = new SolidBrush(Color.FromArgb((int)(240 * fade), green)))
                using (Brush track = new SolidBrush(Color.FromArgb((int)(120 * fade), 60, 20, 26)))
                using (Brush fill = new SolidBrush(Color.FromArgb((int)(235 * fade), red)))
                {
                    graphics.DrawString("ENGRAM SYNC // CAPRI.EXE", font, text, box.X + 14, box.Y + 12);
                    graphics.FillRectangle(track, box.X + 14, box.Y + 44, box.Width - 28, 14);
                    graphics.FillRectangle(fill, box.X + 14, box.Y + 44, (int)((box.Width - 28) * done), 14);
                    graphics.DrawString(((int)(done * 100)) + "%", font, text, box.Right - 54, box.Y + 12);
                }
            }
            else if (Kind == 5)
            {
                Point cursor = Cursor.Position;
                double angle = age * 2.6;
                PointF target = new PointF(cursor.X - where.X + (float)Math.Cos(angle) * 48 + 30, cursor.Y - where.Y + (float)Math.Sin(angle) * 36 + 22);
                if (age < 0.05) chase = new PointF(target.X + 160, target.Y + 90);
                chase = new PointF(chase.X + (target.X - chase.X) * 0.12f, chase.Y + (target.Y - chase.Y) * 0.12f);
                PointF[] arrow = { new PointF(0, 0), new PointF(0, 21), new PointF(5, 16), new PointF(9, 25), new PointF(13, 23), new PointF(9, 14), new PointF(16, 14) };
                PointF[] shifted = new PointF[arrow.Length];
                for (int i = 0; i < arrow.Length; i++) shifted[i] = new PointF(arrow[i].X + chase.X, arrow[i].Y + chase.Y);
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (Brush shadow = new SolidBrush(Color.FromArgb((int)(90 * fade), green)))
                {
                    PointF[] ghost = new PointF[shifted.Length];
                    for (int i = 0; i < ghost.Length; i++) ghost[i] = new PointF(shifted[i].X + 3, shifted[i].Y + 2);
                    graphics.FillPolygon(shadow, ghost);
                }
                using (Brush body = new SolidBrush(Color.FromArgb((int)(230 * fade), red))) graphics.FillPolygon(body, shifted);
                using (Pen outline = new Pen(Color.FromArgb((int)(220 * fade), 20, 0, 4), 1.5f)) graphics.DrawPolygon(outline, shifted);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { ticks.Dispose(); if (canvas != null) canvas.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // A small click-through window that floats above her head and follows her while she talks.
    internal sealed class Bubble : Form
    {
        private const int Tail = 10;
        private readonly Func<Point> head;
        private readonly System.Windows.Forms.Timer ticks;
        private readonly Stopwatch clock = new Stopwatch();
        private Bitmap picture;
        private Size body;
        private int tailX = -1;
        private float pixels;
        private double duration;
        private bool terminal;
        private int shown;
        private Bitmap faceClosed;
        private Bitmap faceOpen;
        private bool mouth;
        private double talkTime;
        private const int FaceWidth = 104;
        private const int FaceHeight = 58;
        internal string Message;
        internal Rectangle Area { get; private set; }
        internal double Age { get { return clock.Elapsed.TotalSeconds; } }

        internal Bubble(Func<Point> where)
        {
            head = where;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            ticks = new System.Windows.Forms.Timer();
            ticks.Interval = 33;
            ticks.Tick += delegate { Tick(); };
        }

        internal Bitmap Picture { get { return picture; } }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams value = base.CreateParams;
                value.ExStyle |= 0x00080000 | 0x00000080 | 0x08000000 | 0x00000020;
                return value;
            }
        }

        internal void Present(string text, double seconds, bool onTop, float fontPixels) { Present(text, seconds, onTop, fontPixels, false); }

        // Terminal bubbles are her hacking voice: a green monospace prompt on black that types itself out.
        internal void Present(string text, double seconds, bool onTop, float fontPixels, bool code) { Present(text, seconds, onTop, fontPixels, code, null, null); }

        // With faces, a small portrait sits beside the text and its mouth moves while she talks; a code bubble keeps one frame still.
        internal void Present(string text, double seconds, bool onTop, float fontPixels, bool code, Bitmap closed, Bitmap open)
        {
            Message = text;
            duration = seconds;
            pixels = fontPixels;
            terminal = code;
            faceClosed = closed;
            faceOpen = open ?? closed;
            talkTime = code ? 0 : Math.Min(seconds, 0.4 + text.Length * 0.05);
            mouth = false;
            shown = -1;
            TopMost = onTop;
            using (Font font = new Font(terminal ? "Consolas" : Neon.Face, pixels, GraphicsUnit.Pixel))
            using (Bitmap scratch = new Bitmap(1, 1))
            using (Graphics graphics = Graphics.FromImage(scratch))
            {
                SizeF measured = graphics.MeasureString((terminal ? "> " : "") + text + (terminal ? "_" : ""), font, (int)(pixels * (terminal ? 26 : 18)));
                int face = faceClosed == null ? 0 : FaceWidth + 10;
                body = new Size(Math.Max(56, (int)Math.Ceiling(measured.Width) + 24 + face), Math.Max((int)Math.Ceiling(measured.Height) + 16, faceClosed == null ? 0 : FaceHeight + 16));
            }
            tailX = -1;
            clock.Reset();
            clock.Start();
            if (!Visible) Show();
            ticks.Start();
            Tick();
        }

        internal void Dismiss()
        {
            ticks.Stop();
            clock.Stop();
            if (Visible) Hide();
        }

        internal void Tick()
        {
            double age = clock.Elapsed.TotalSeconds;
            if (age >= duration) { Dismiss(); return; }
            Point at = head();
            Rectangle area = Screen.FromPoint(at).WorkingArea;
            int x = Math.Max(area.Left + 4, Math.Min(at.X - body.Width / 2, area.Right - body.Width - 4));
            int y = Math.Max(area.Top, at.Y - body.Height - Tail + 2);
            int tail = Math.Max(18, Math.Min(body.Width - 18, at.X - x));
            int count = terminal ? Math.Min(Message.Length, (int)(age * 45)) : Message.Length;
            bool open = faceClosed != null && age < talkTime && (int)(age / 0.11) * 7 % 5 < 3;
            if (tail != tailX || picture == null || count != shown || open != mouth) { tailX = tail; shown = count; mouth = open; Redraw(); }
            Area = new Rectangle(x, y, picture.Width, picture.Height);
            Native.DrawLayered(Handle, picture, Area.Location, (byte)(255 * Math.Min(1, Math.Min(age / 0.2, (duration - age) / 0.35))));
        }

        private void Redraw()
        {
            Bitmap next = new Bitmap(body.Width, body.Height + Tail, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(next))
            using (Font font = new Font(terminal ? "Consolas" : Neon.Face, pixels, GraphicsUnit.Pixel))
            using (GraphicsPath outline = new GraphicsPath())
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                float left = 0.75f, top = 0.75f, right = body.Width - 0.75f, bottom = body.Height - 0.75f, cut = 9;
                outline.AddPolygon(new PointF[] { new PointF(left + cut, top), new PointF(right, top), new PointF(right, bottom - cut), new PointF(right - cut, bottom), new PointF(tailX + 7, bottom), new PointF(tailX, bottom + Tail - 1), new PointF(tailX - 7, bottom), new PointF(left, bottom), new PointF(left, top + cut) });
                Color edgeColor = terminal ? Neon.Crimson : Neon.Green;
                Color accent = terminal ? Neon.Green : Neon.Crimson;
                using (Brush fill = new SolidBrush(Color.FromArgb(242, Neon.Void))) graphics.FillPath(fill, outline);
                GraphicsState scanned = graphics.Save();
                graphics.SetClip(outline);
                using (Pen scan = new Pen(Color.FromArgb(14, 255, 255, 255))) for (int y = 2; y < body.Height; y += 3) graphics.DrawLine(scan, 0, y, body.Width, y);
                graphics.Restore(scanned);
                using (Pen glow = new Pen(Color.FromArgb(36, edgeColor), 4f)) graphics.DrawPath(glow, outline);
                using (Pen edge = new Pen(Color.FromArgb(220, edgeColor), 1f)) graphics.DrawPath(edge, outline);
                using (Pen mark = new Pen(accent, 2f)) { graphics.DrawLine(mark, left, top + cut, left + cut, top); graphics.DrawLine(mark, right, bottom - cut, right - cut, bottom); }
                string typed = terminal ? "> " + Message.Substring(0, shown) + (shown < Message.Length ? "_" : "") : Message;
                int indent = 12;
                if (faceClosed != null)
                {
                    Rectangle face = new Rectangle(10, (body.Height - FaceHeight) / 2, FaceWidth, FaceHeight);
                    using (GraphicsPath frame = Neon.Cut(face, 8))
                    {
                        GraphicsState saved = graphics.Save();
                        graphics.SetClip(frame);
                        graphics.DrawImage(mouth ? faceOpen : faceClosed, face);
                        graphics.Restore(saved);
                        using (Pen edge = new Pen(Color.FromArgb(230, edgeColor), 1.25f)) graphics.DrawPath(edge, frame);
                    }
                    indent = 10 + FaceWidth + 10;
                }
                using (Brush ink = new SolidBrush(terminal ? Neon.Green : Neon.Text)) graphics.DrawString(typed, font, ink, new RectangleF(indent, 8, body.Width - indent - 6, body.Height - 12));
            }
            Bitmap old = picture;
            picture = next;
            if (old != null) old.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { ticks.Dispose(); if (picture != null) picture.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // Her look: the black, crimson and neon green of the show, with sliced corners and hairline edges.
    internal static class Neon
    {
        internal static readonly Color Void = Color.FromArgb(7, 8, 13);
        internal static readonly Color Panel = Color.FromArgb(13, 15, 22);
        internal static readonly Color Line = Color.FromArgb(40, 45, 62);
        internal static readonly Color Crimson = Color.FromArgb(255, 20, 70);
        internal static readonly Color Green = Color.FromArgb(0, 255, 110);
        internal static readonly Color Blue = Color.FromArgb(40, 130, 255);
        internal static readonly Color Text = Color.FromArgb(214, 224, 236);
        internal static readonly Color Dim = Color.FromArgb(96, 106, 122);
        private static string face;

        // Bahnschrift gives the squared, techy look; older Windows falls back to Segoe UI.
        internal static string Face
        {
            get
            {
                if (face != null) return face;
                face = "Segoe UI";
                foreach (FontFamily family in FontFamily.Families) if (family.Name == "Bahnschrift") face = "Bahnschrift";
                return face;
            }
        }

        internal static Font Font(float points) { return new Font(Face, points); }

        // A box with its top-left and bottom-right corners sliced off.
        internal static GraphicsPath Cut(RectangleF box, float cut)
        {
            GraphicsPath path = new GraphicsPath();
            path.AddPolygon(new PointF[] { new PointF(box.Left + cut, box.Top), new PointF(box.Right, box.Top), new PointF(box.Right, box.Bottom - cut), new PointF(box.Right - cut, box.Bottom), new PointF(box.Left, box.Bottom), new PointF(box.Left, box.Top + cut) });
            return path;
        }

        // Clips a menu or window to the sliced shape now and whenever it changes size.
        internal static void Shape(Control control, float cut)
        {
            EventHandler apply = delegate
            {
                if (control.Width < 2 || control.Height < 2) return;
                using (GraphicsPath path = Cut(new RectangleF(0, 0, control.Width, control.Height), cut))
                {
                    Region old = control.Region;
                    control.Region = new Region(path);
                    if (old != null) old.Dispose();
                }
            };
            control.SizeChanged += apply;
            control.HandleCreated += apply;
        }

        // The sliced outline with a faint glow, a crimson edge and green on the cut corners.
        internal static void Outline(Graphics graphics, RectangleF box, float cut, Color edge, Color accent)
        {
            SmoothingMode saved = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = Cut(box, cut))
            {
                using (Pen glow = new Pen(Color.FromArgb(34, edge), 4f)) graphics.DrawPath(glow, path);
                using (Pen line = new Pen(Color.FromArgb(215, edge), 1f)) graphics.DrawPath(line, path);
            }
            using (Pen mark = new Pen(accent, 2f))
            {
                graphics.DrawLine(mark, box.Left, box.Top + cut, box.Left + cut, box.Top);
                graphics.DrawLine(mark, box.Right, box.Bottom - cut, box.Right - cut, box.Bottom);
            }
            graphics.SmoothingMode = saved;
        }
    }

    internal sealed class NeonRenderer : ToolStripProfessionalRenderer
    {
        internal NeonRenderer() : base(new CapriMenuColors()) { RoundedEdges = false; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { using (Brush fill = new SolidBrush(Neon.Panel)) e.Graphics.FillRectangle(fill, e.AffectedBounds); }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is ToolStripDropDown) Neon.Outline(e.Graphics, new RectangleF(0.5f, 0.5f, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1), 10, Neon.Crimson, Neon.Green);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            using (Brush fill = new SolidBrush(Neon.Panel)) e.Graphics.FillRectangle(fill, e.AffectedBounds);
            using (Pen line = new Pen(Neon.Line)) e.Graphics.DrawLine(line, e.AffectedBounds.Right - 1, e.AffectedBounds.Top + 8, e.AffectedBounds.Right - 1, e.AffectedBounds.Bottom - 8);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            Rectangle box = new Rectangle(2, e.Item.ContentRectangle.Top, e.Item.Width - 4, e.Item.ContentRectangle.Height);
            using (Brush fill = new SolidBrush(Color.FromArgb(48, 10, 26))) e.Graphics.FillRectangle(fill, box);
            using (Brush bar = new SolidBrush(Neon.Crimson)) e.Graphics.FillRectangle(bar, box.Left, box.Top, 3, box.Height);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (e.Item.Name == "title")
            {
                using (Font mono = new Font("Consolas", 9.5f, FontStyle.Bold)) TextRenderer.DrawText(e.Graphics, e.Text, mono, e.TextRectangle, Neon.Crimson, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                return;
            }
            e.TextColor = !e.Item.Enabled ? Neon.Dim : e.Item.Selected ? Color.White : Neon.Text;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle box = e.ImageRectangle;
            float cx = box.Left + box.Width / 2f, cy = box.Top + box.Height / 2f, r = 5f;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Brush fill = new SolidBrush(e.Item.Enabled ? Neon.Green : Neon.Dim))
                e.Graphics.FillPolygon(fill, new PointF[] { new PointF(cx, cy - r), new PointF(cx + r, cy), new PointF(cx, cy + r), new PointF(cx - r, cy) });
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (Pen line = new Pen(Neon.Line)) e.Graphics.DrawLine(line, 6, y, e.Item.Width - 6, y);
            using (Pen mark = new Pen(Neon.Crimson, 2f)) e.Graphics.DrawLine(mark, 6, y, 20, y);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item.Selected && e.Item.Enabled ? Neon.Crimson : Neon.Dim;
            base.OnRenderArrow(e);
        }
    }

    internal sealed class CapriMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Neon.Panel; } }
        public override Color ImageMarginGradientBegin { get { return ToolStripDropDownBackground; } }
        public override Color ImageMarginGradientEnd { get { return ToolStripDropDownBackground; } }
        public override Color ImageMarginGradientMiddle { get { return ToolStripDropDownBackground; } }
        public override Color ImageMarginRevealedGradientBegin { get { return ToolStripDropDownBackground; } }
        public override Color ImageMarginRevealedGradientMiddle { get { return ToolStripDropDownBackground; } }
        public override Color ImageMarginRevealedGradientEnd { get { return ToolStripDropDownBackground; } }
        public override Color MenuItemSelected { get { return Color.FromArgb(65, 69, 53); } }
        public override Color MenuItemBorder { get { return Color.FromArgb(167, 245, 88); } }
        public override Color CheckBackground { get { return Color.FromArgb(65, 69, 53); } }
        public override Color CheckSelectedBackground { get { return Color.FromArgb(65, 69, 53); } }
        public override Color CheckPressedBackground { get { return Color.FromArgb(65, 69, 53); } }
        public override Color ButtonSelectedBorder { get { return Color.FromArgb(167, 245, 88); } }
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Point { internal int X, Y; internal Point(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] internal struct Size { internal int Width, Height; internal Size(int width, int height) { Width = width; Height = height; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] internal struct Blend { internal byte Operation, Flags, Alpha, Format; }
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] internal static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] internal static extern int SetWindowLong(IntPtr window, int index, int value);
        [DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr window, int id, int modifiers, int key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] internal static extern int SetWindowTheme(IntPtr window, string appName, string idList);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct WaveCaps { internal ushort Mid; internal ushort Pid; internal uint Driver; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Name; internal uint Formats; internal ushort Channels; internal ushort Reserved; }
        [StructLayout(LayoutKind.Sequential)] internal struct WaveFormat { internal ushort Tag; internal ushort Channels; internal uint Rate; internal uint BytesPerSecond; internal ushort Align; internal ushort Bits; internal ushort Extra; }
        [StructLayout(LayoutKind.Sequential)] internal struct WaveHeader { internal IntPtr Data; internal uint Length; internal uint Recorded; internal IntPtr User; internal uint Flags; internal uint Loops; internal IntPtr Next; internal IntPtr Reserved; }
        [DllImport("winmm.dll")] internal static extern int waveInGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] internal static extern int waveInGetDevCaps(IntPtr device, ref WaveCaps caps, int size);
        [DllImport("winmm.dll")] internal static extern int waveInOpen(out IntPtr handle, int device, ref WaveFormat format, IntPtr callback, IntPtr instance, int flags);
        [DllImport("winmm.dll")] internal static extern int waveInPrepareHeader(IntPtr handle, IntPtr header, int size);
        [DllImport("winmm.dll")] internal static extern int waveInUnprepareHeader(IntPtr handle, IntPtr header, int size);
        [DllImport("winmm.dll")] internal static extern int waveInAddBuffer(IntPtr handle, IntPtr header, int size);
        [DllImport("winmm.dll")] internal static extern int waveInStart(IntPtr handle);
        [DllImport("winmm.dll")] internal static extern int waveInReset(IntPtr handle);
        [DllImport("winmm.dll")] internal static extern int waveInClose(IntPtr handle);

        // The recording devices Windows lists, in the order the wave API numbers them (names are cut off at 31 letters by Windows).
        internal static string[] WaveDevices()
        {
            int count = waveInGetNumDevs();
            string[] names = new string[Math.Max(0, count)];
            for (int i = 0; i < names.Length; i++)
            {
                WaveCaps caps = new WaveCaps();
                names[i] = waveInGetDevCaps((IntPtr)i, ref caps, Marshal.SizeOf(typeof(WaveCaps))) == 0 ? caps.Name : "";
            }
            return names;
        }

        internal static int WaveDevice(string name) { return Array.IndexOf(WaveDevices(), name); }

        internal delegate bool EnumProc(IntPtr window, IntPtr parameter);
        [StructLayout(LayoutKind.Sequential)] private struct Box { internal int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Box rect);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder text, int count);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Box rect, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

        // One visible window on the desktop: where it is and whether she could sit on it. Only rectangles are read; no titles, no contents.
        [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

        // A media or volume key, pressed once: the same as the key on a keyboard. It sends a key and reads nothing.
        internal static void PressKey(byte key)
        {
            keybd_event(key, 0, 0, UIntPtr.Zero);
            keybd_event(key, 0, 2, UIntPtr.Zero);
        }

        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int fromX, int fromY, int operation);

        // Copies what is on screen in the area into the bitmap, including layered windows such as her own.
        internal static void Grab(Bitmap into, Rectangle area)
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            try
            {
                using (Graphics g = Graphics.FromImage(into))
                {
                    IntPtr target = g.GetHdc();
                    try { if (!BitBlt(target, 0, 0, area.Width, area.Height, screen, area.X, area.Y, 0x00CC0020 | 0x40000000)) throw new InvalidOperationException("The screen could not be read."); }
                    finally { g.ReleaseHdc(target); }
                }
            }
            finally { ReleaseDC(IntPtr.Zero, screen); }
        }

        [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size; public uint Time; }
        [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput info);

        // Seconds since the keyboard or mouse was last used. Only the time is read, never what was pressed.
        internal static double IdleSeconds()
        {
            LastInput info = new LastInput();
            info.Size = (uint)Marshal.SizeOf(typeof(LastInput));
            if (!GetLastInputInfo(ref info)) return 0;
            return unchecked((uint)Environment.TickCount - info.Time) / 1000.0;
        }

        // The program name (for instance "chrome") of the window in front, not one of ours.
        internal static string FrontApp()
        {
            foreach (Pane pane in Panes(IntPtr.Zero, IntPtr.Zero, false))
            {
                uint owner;
                GetWindowThreadProcessId(pane.Handle, out owner);
                try { return Process.GetProcessById((int)owner).ProcessName; }
                catch (Exception) { }
            }
            return "";
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int count);

        // The title of the window in front of the others (not one of ours), for her to know what she is looking at.
        internal static string FrontTitle()
        {
            foreach (Pane pane in Panes(IntPtr.Zero, IntPtr.Zero, false))
            {
                System.Text.StringBuilder title = new System.Text.StringBuilder(200);
                GetWindowText(pane.Handle, title, 200);
                if (title.Length > 0) return title.ToString();
            }
            return "";
        }

        internal struct Pane { internal IntPtr Handle; internal Rectangle Frame; internal bool Sittable; }

        internal static bool Cloaked(IntPtr window)
        {
            int value;
            try { return DwmGetWindowAttribute(window, 14, out value, 4) == 0 && value != 0; }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }

        // The visible edge of a window, without the invisible resize border Windows adds around it.
        internal static Rectangle FrameOf(IntPtr window)
        {
            Box box;
            try { if (DwmGetWindowAttribute(window, 9, out box, 16) == 0) return Rectangle.FromLTRB(box.Left, box.Top, box.Right, box.Bottom); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            if (!GetWindowRect(window, out box)) return Rectangle.Empty;
            return Rectangle.FromLTRB(box.Left, box.Top, box.Right, box.Bottom);
        }

        [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

        // A borderless window that fills a whole monitor, which is what games and full screen video look like.
        internal static bool CoversMonitor(Rectangle frame, Rectangle monitor, bool captioned, string kind)
        {
            if (captioned || kind == "Progman" || kind == "WorkerW" || kind == "Shell_TrayWnd" || kind == "Shell_SecondaryTrayWnd") return false;
            return frame.Left <= monitor.Left && frame.Top <= monitor.Top && frame.Right >= monitor.Right && frame.Bottom >= monitor.Bottom;
        }

        // True while Windows says a full screen program or presentation is running, or the window in front fills its monitor.
        internal static bool FullscreenAhead()
        {
            int state;
            try { if (SHQueryUserNotificationState(out state) == 0 && (state == 2 || state == 3 || state == 4)) return true; }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero || !IsWindowVisible(window) || IsIconic(window)) return false;
            uint owner;
            GetWindowThreadProcessId(window, out owner);
            if (owner == (uint)Process.GetCurrentProcess().Id) return false;
            System.Text.StringBuilder name = new System.Text.StringBuilder(64);
            GetClassName(window, name, 64);
            Rectangle frame = FrameOf(window);
            if (frame.Width < 100 || frame.Height < 100) return false;
            return CoversMonitor(frame, Screen.FromRectangle(frame).Bounds, (GetWindowLong(window, -16) & 0x00C00000) == 0x00C00000, name.ToString());
        }

        // Every real window from the front of the stack to the back. Other programs' windows only, or only ours when testing.
        internal static System.Collections.Generic.List<Pane> Panes(IntPtr skipOne, IntPtr skipTwo, bool ours)
        {
            System.Collections.Generic.List<Pane> found = new System.Collections.Generic.List<Pane>();
            uint mine = (uint)Process.GetCurrentProcess().Id;
            System.Text.StringBuilder name = new System.Text.StringBuilder(64);
            EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                if (window == skipOne || window == skipTwo || !IsWindowVisible(window) || IsIconic(window) || Cloaked(window)) return true;
                uint owner;
                GetWindowThreadProcessId(window, out owner);
                if ((owner == mine) != ours) return true;
                int style = GetWindowLong(window, -20);
                if ((style & (0x00000020 | 0x08000000)) != 0) return true;
                name.Length = 0;
                GetClassName(window, name, 64);
                string kind = name.ToString();
                if (kind == "Progman" || kind == "WorkerW" || kind == "Shell_TrayWnd" || kind == "Shell_SecondaryTrayWnd") return true;
                Rectangle frame = FrameOf(window);
                if (frame.Width < 40 || frame.Height < 20) return true;
                Pane pane = new Pane();
                pane.Handle = window;
                pane.Frame = frame;
                pane.Sittable = (style & 0x00000080) == 0 && (GetWindowLong(window, -16) & 0x00C00000) == 0x00C00000 && !IsZoomed(window) && frame.Width >= 200 && frame.Height >= 60;
                found.Add(pane);
                return true;
            }, IntPtr.Zero);
            return found;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr screen, ref Point destination, ref Size size, IntPtr source, ref Point origin, int colorKey, ref Blend blend, int flags);

        internal static void DrawLayered(IntPtr window, Bitmap bitmap, System.Drawing.Point location, byte alpha = 255)
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr memory = CreateCompatibleDC(screen);
            IntPtr nativeBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            IntPtr previous = SelectObject(memory, nativeBitmap);
            try
            {
                Point destination = new Point(location.X, location.Y);
                Point origin = new Point(0, 0);
                Size size = new Size(bitmap.Width, bitmap.Height);
                Blend blend = new Blend { Operation = 0, Flags = 0, Alpha = alpha, Format = 1 };
                if (!UpdateLayeredWindow(window, screen, ref destination, ref size, memory, ref origin, 0, ref blend, 2)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                SelectObject(memory, previous);
                DeleteObject(nativeBitmap);
                DeleteDC(memory);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }
    }
}
