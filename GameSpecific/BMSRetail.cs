﻿using LiveSplit.ComponentUtil;
using System;
using System.Diagnostics;
using System.Linq;
using System.Media;

namespace LiveSplit.SourceSplit.GameSpecific
{
    class BMSRetail : GameSupport
    {
        // how to match with demos:
        // start: on map load
        // xen start: when view entity changes back to the player's
        // ending: first tick nihilanth's health is zero
        // earthbound ending: when view entity changes to the ending camera's

        // hc mod start: when the tram door is opening
        // hc mod end: when the flash sprites disappears

        private bool _onceFlag;

        // offsets and binary sizes
        private int _baseEntityHealthOffset = -1;
        private int _baseEffectsFlagsOffset = -1;
        private const int _serverModernModuleSize = 0x9D6000;
        private const int _serverModModuleSize = 0x81B000;
        private const int _nihiPhaseCounterOffset = 0x1a6e4;

        private StringWatcher _command;
        private bool _handleInputCommandEnabled = true;

        // earthbound start
        private string _ebEndMap = "bm_c3a2i";
        private bool _ebEnd = false;
        private int _ebCamIndex;

        // xen start & run end
        private const string _xenStartMap = "bm_c4a1a";
        private bool _xenStart = false;
        private bool _xenSplit = false;
        private bool _nihiSplit = false;
        private MemoryWatcher<int> _nihiHP;
        private MemoryWatcher<int> _nihiPhaseCounter;
        private int _xenCamIndex;

        // hc mod start & end
        private const string _hcStartMap = "hc_t0a0";
        private Vector3f _hcStartDoorTargPos = new Vector3f(4152.7f, -2853.1f, 105f);
        private MemoryWatcher<Vector3f> _hcStartDoorPos;
        private const string _hcEndMap = "hc_t0a3";
        private MemoryWatcher<uint> _hcEndSpriteFlags;

        public BMSRetail()
        {
            this.GameTimingMethod = GameTimingMethod.EngineTicksWithPauses;
            this.StartOnFirstLoadMaps.Add("bm_c1a0a");
            this.FirstMap = "bm_c1a0a";
            this.LastMap = "bm_c4a4a";
            this.RequiredProperties = PlayerProperties.ViewEntity;
        }

        public override void OnGameAttached(GameState state)
        {
            ProcessModuleWow64Safe server = state.GameProcess.ModulesWow64Safe().FirstOrDefault(x => x.ModuleName.ToLower() == "server.dll");
            Trace.Assert(server != null);

            var scanner = new SignatureScanner(state.GameProcess, server.BaseAddress, server.ModuleMemorySize);
            var commandTarg = new SigScanTarget(16, "55 8B EC 8D 45 ?? 50 FF 75 ?? 68 00 04 00 00 68 ?? ?? ?? ??");

            IntPtr commandPtr = state.GameProcess.ReadPointer(scanner.Scan(commandTarg));
            if (commandPtr != IntPtr.Zero)
                Debug.WriteLine("Command ptr found at 0x" + commandPtr.ToString("X"));
            else
                Debug.WriteLine("Command ptr not found!");

            if (GameMemory.GetBaseEntityMemberOffset("m_iHealth", state.GameProcess, scanner, out _baseEntityHealthOffset))
                Debug.WriteLine("CBaseEntity::m_iHealth offset = 0x" + _baseEntityHealthOffset.ToString("X"));

            if (GameMemory.GetBaseEntityMemberOffset("m_fEffects", state.GameProcess, scanner, out _baseEffectsFlagsOffset))
                Debug.WriteLine("CBaseEntity::m_fEffects offset = 0x" + _baseEffectsFlagsOffset.ToString("X"));


            _handleInputCommandEnabled = true;
            // for versions before .91, disable handleinputcommand as it's redundant
            if (server.ModuleMemorySize < _serverModernModuleSize)
            {
                _ebEnd = true;
                _handleInputCommandEnabled = false;
                // for mod, eb's final map name is different
                if (server.ModuleMemorySize <= _serverModModuleSize)
                    _ebEndMap = "bm_c3a2h";
            }

            if (_handleInputCommandEnabled)
            {
                _command = new StringWatcher(commandPtr + 0x11, 20);

                // if livesplit is loaded after the player has put in a valid command, then check
                HandleInputCommand(state, true);
            }
        }

        public override void OnSessionStart(GameState state)
        {
            base.OnSessionStart(state);

            _onceFlag = false;

            if (state.CurrentMap.ToLower() == _ebEndMap)
            {
                _ebCamIndex = state.GetEntIndexByName("locked_in");
            }

            else if (state.CurrentMap.ToLower() == _xenStartMap)
            {
                _xenCamIndex = state.GetEntIndexByName("stand_viewcontrol");
            }

            else if (this.IsLastMap && state.PlayerEntInfo.EntityPtr != IntPtr.Zero)
            {
                IntPtr nihiPtr = state.GetEntityByName("nihilanth");
                Debug.WriteLine("Nihilanth pointer = 0x" + nihiPtr.ToString("X"));

                _nihiHP = new MemoryWatcher<int>(nihiPtr + _baseEntityHealthOffset);
                _nihiPhaseCounter = new MemoryWatcher<int>(nihiPtr + _nihiPhaseCounterOffset);
            }

            else if (state.CurrentMap.ToLower() == _hcStartMap)
            {
                _hcStartDoorPos = new MemoryWatcher<Vector3f>(state.GetEntityByName("tram_door_door_out") + state.GameOffsets.BaseEntityAbsOriginOffset);
            }

            else if (state.CurrentMap.ToLower() == _hcEndMap)
            {
                _hcEndSpriteFlags = new MemoryWatcher<uint>(state.GetEntityByName("spr_camera_flash") + _baseEffectsFlagsOffset);
            }
        }

        // allow disabling and enabling of features through monitoring specific console input
        // format: ebend<arg>, xenstart<arg>, eg: ebend1, xenstart0, characters are also accepted which will be interpreted as true
        void HandleArg(string command, string name, ref bool target)
        {
            string arg = command.Substring(command.Length - 1, 1);
            if (arg != "0") target = true;
            else target = false;

            Debug.WriteLine(name + " is " + ((arg != "0") ? "Enabled" : "Disabled"));

            // play the warning sound to let people know its toggled
            SystemSounds.Asterisk.Play();
        }

        bool CheckCommand(string cmd, string targetCmd)
        {
            return cmd.Length - 1 == (targetCmd).Length && cmd.Substring(0, cmd.Length - 1) == targetCmd;
        }

        void HandleInputCommand(GameState state, bool ignoreChanged = false)
        {
            if (!_handleInputCommandEnabled)
                return;

            _command.Update(state.GameProcess);
            if (ignoreChanged || _command.Changed)
            {
                // remove any carriage returns
                string cleanedCmd = _command.Current.Replace("\n", "").Replace("\r", "").ToLower();
                if (CheckCommand(cleanedCmd, "ebend"))
                {
                    HandleArg(cleanedCmd, "Earthbound Auto-end", ref _ebEnd);
                }
                else if (cleanedCmd.Contains("xen"))
                {
                    if (cleanedCmd.Contains("start"))
                    {
                        HandleArg(cleanedCmd, "Xen Auto-start", ref _xenStart);
                        if (_xenStart)
                        {
                            Debug.WriteLine("Xen Auto-split is now disabled");
                            _xenSplit = false;
                        }
                    }
                    else if (cleanedCmd.Contains("split"))
                    {
                        HandleArg(cleanedCmd, "Xen Auto-split", ref _xenSplit);
                        if (_xenSplit)
                        {
                            Debug.WriteLine("Xen Auto-start is now disabled");
                            _xenStart = false;
                        }
                    }    
                }
                else if (CheckCommand(cleanedCmd, "nihisplit"))
                {
                    HandleArg(cleanedCmd, "Nihilanth splits", ref _nihiSplit);
                }
            }
        }

        public GameSupportResult DefaultEnd(string endingname)
        {
            _onceFlag = true;
            Debug.WriteLine(endingname);
            return GameSupportResult.PlayerLostControl;
        }

        public override GameSupportResult OnUpdate(GameState state)
        {
            HandleInputCommand(state);

            if (_onceFlag)
                return GameSupportResult.DoNothing;

            if (this.IsLastMap)
            {
                _nihiHP.Update(state.GameProcess);
                if (_nihiHP.Current <= 0 && _nihiHP.Old > 0)
                {
                    return DefaultEnd("black mesa end");
                }

                if (_nihiSplit)
                {
                    _nihiPhaseCounter.Update(state.GameProcess);

                    if (_nihiPhaseCounter.Current - _nihiPhaseCounter.Old == 1 && _nihiPhaseCounter.Old != 0)
                    {
                        Debug.WriteLine("black mesa nihilanth phase " + _nihiPhaseCounter.Old + " end");
                        return GameSupportResult.PlayerLostControl;
                    }
                }
            }
            else if (_ebEnd && state.CurrentMap.ToLower() == _ebEndMap)
            {
                if (state.PlayerViewEntityIndex == _ebCamIndex && state.PrevPlayerViewEntityIndex == 1)
                {
                    return DefaultEnd("bms eb end");
                }
            }
            else if ((_xenStart || _xenSplit) && state.CurrentMap.ToLower() == _xenStartMap)
            {
                if (state.PlayerViewEntityIndex == 1 && state.PrevPlayerViewEntityIndex == _xenCamIndex)
                {
                    _onceFlag = true;
                    Debug.WriteLine("bms xen start");
                    return _xenStart ? GameSupportResult.PlayerGainedControl : GameSupportResult.PlayerLostControl;
                }
            }
            else if (state.CurrentMap.ToLower() == _hcStartMap)
            {
                _hcStartDoorPos.Update(state.GameProcess);

                if (_hcStartDoorPos.Old.Distance(_hcStartDoorTargPos) > 0.05f
                    && _hcStartDoorPos.Current.Distance(_hcStartDoorTargPos) <= 0.05f)
                {
                    return DefaultEnd("bms hc mod start");
                }
            }
            else if (state.CurrentMap.ToLower() == _hcEndMap)
            {
                _hcEndSpriteFlags.Update(state.GameProcess);

                if (state.TickCount >= 10 && (_hcEndSpriteFlags.Old & 0x20) == 0 &&
                    (_hcEndSpriteFlags.Current & 0x20) != 0)
                {
                    return DefaultEnd("bms hc mod end");
                }
            }
            return GameSupportResult.DoNothing;
        }
    }
}