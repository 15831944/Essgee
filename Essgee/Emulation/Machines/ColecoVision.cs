﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Essgee.Emulation.Configuration;
using Essgee.Emulation.CPU;
using Essgee.Emulation.Video;
using Essgee.Emulation.Audio;
using Essgee.Emulation.Cartridges;
using Essgee.Emulation.Cartridges.Coleco;
using Essgee.EventArguments;
using Essgee.Exceptions;
using Essgee.Utilities;

namespace Essgee.Emulation.Machines
{
	[MachineIndex(4)]
	public class ColecoVision : IMachine
	{
		// TODO: accuracy, bugfixes, PAL machines??

		const double masterClock = 10738635;
		const double refreshRate = 59.922743;

		const int ramSize = 1 * 1024;

		double vdpClock, psgClock;

		public event EventHandler<SendLogMessageEventArgs> SendLogMessage;
		protected virtual void OnSendLogMessage(SendLogMessageEventArgs e) { SendLogMessage?.Invoke(this, e); }

		public event EventHandler<EventArgs> EmulationReset;
		protected virtual void OnEmulationReset(EventArgs e) { EmulationReset?.Invoke(this, e); }

		public event EventHandler<RenderScreenEventArgs> RenderScreen
		{
			add { vdp.RenderScreen += value; }
			remove { vdp.RenderScreen -= value; }
		}

		public event EventHandler<SizeScreenEventArgs> SizeScreen
		{
			add { vdp.SizeScreen += value; }
			remove { vdp.SizeScreen -= value; }
		}

		public event EventHandler<ChangeViewportEventArgs> ChangeViewport;
		protected virtual void OnChangeViewport(ChangeViewportEventArgs e) { ChangeViewport?.Invoke(this, e); }

		public event EventHandler<PollInputEventArgs> PollInput;
		protected virtual void OnPollInput(PollInputEventArgs e) { PollInput?.Invoke(this, e); }

		public event EventHandler<EnqueueSamplesEventArgs> EnqueueSamples
		{
			add { psg.EnqueueSamples += value; }
			remove { psg.EnqueueSamples -= value; }
		}

		public event EventHandler<GetGameMetadataEventArgs> GetGameMetadata;
		protected virtual void OnGetGameMetadata(GetGameMetadataEventArgs e) { GetGameMetadata?.Invoke(this, e); }

		public string ManufacturerName => "Coleco";
		public string ModelName => "ColecoVision";
		public string DatFilename => "Coleco - ColecoVision.dat";
		public (string Extension, string Description) FileFilter => (".col", "ColecoVision ROMs");
		public bool HasBootstrap => true;
		public double RefreshRate => refreshRate;
		public double PixelAspectRatio => 8.0 / 7.0;

		ICartridge bios, cartridge;
		byte[] wram;
		Z80A cpu;
		TMS99xxA vdp;
		SN76489 psg;

		[Flags]
		enum KeyJoyButtons : ushort
		{
			None = 0x0000,
			KeyNumber6 = 0x0001,
			KeyNumber1 = 0x0002,
			KeyNumber3 = 0x0003,
			KeyNumber9 = 0x0004,
			KeyNumber0 = 0x0005,
			KeyStarKey = 0x0006,
			KeyInvalid8 = 0x0007,
			KeyNumber2 = 0x0008,
			KeyNumberSignKey = 0x0009,
			KeyNumber7 = 0x000A,
			KeyInvalid4 = 0x000B,
			KeyNumber5 = 0x000C,
			KeyNumber4 = 0x000D,
			KeyNumber8 = 0x000E,
			KeyMask = 0x000F,
			JoyRightButton = 0x0040,
			JoyUp = 0x0100,
			JoyRight = 0x0200,
			JoyDown = 0x0400,
			JoyLeft = 0x0800,
			JoyLeftButton = 0x4000,
			JoyMask = 0x0F40,
		}

		ushort portControls1, portControls2;
		byte controlsReadMode;

		bool isNmi, isNmiPending;

		int currentMasterClockCyclesInFrame, totalMasterClockCyclesInFrame;

		Configuration.ColecoVision configuration;

		public ColecoVision() { }

		public void Initialize()
		{
			bios = null;
			cartridge = null;

			wram = new byte[ramSize];
			cpu = new Z80A(ReadMemory, WriteMemory, ReadPort, WritePort);
			vdp = new TMS99xxA();
			psg = new SN76489();

			vdp.EndOfScanline += (s, e) =>
			{
				PollInputEventArgs pollInputEventArgs = new PollInputEventArgs();
				OnPollInput(pollInputEventArgs);
				ParseInput(pollInputEventArgs);
			};
		}

		public void SetConfiguration(IConfiguration config)
		{
			configuration = (Configuration.ColecoVision)config;

			ReconfigureSystem();
		}

		public void SetRuntimeOption(string name, object value)
		{
			switch (name)
			{
				case nameof(GraphicsEnableState):
					vdp.GraphicsEnableStates = (GraphicsEnableState)value;
					break;

				case nameof(SoundEnableState):
					psg.SoundEnableStates = (SoundEnableState)value;
					break;
			}
		}

		private void ReconfigureSystem()
		{
			vdpClock = (masterClock / 1.0);
			psgClock = (masterClock / 3.0);

			vdp?.SetClockRate(vdpClock);
			vdp?.SetRefreshRate(refreshRate);
			vdp?.SetRevision(0);

			psg?.SetSampleRate(Program.Configuration.SampleRate);
			psg?.SetOutputChannels(2);
			psg?.SetClockRate(psgClock);
			psg?.SetRefreshRate(refreshRate);

			currentMasterClockCyclesInFrame = 0;
			totalMasterClockCyclesInFrame = (int)Math.Round(masterClock / refreshRate);

			OnChangeViewport(new ChangeViewportEventArgs(vdp.Viewport));
		}

		private void LoadBios()
		{
			var (type, bootstrapRomData) = CartridgeLoader.Load(configuration.BiosRom, "ColecoVision BIOS");
			bios = new ColecoCartridge(bootstrapRomData.Length, 0);
			bios.LoadRom(bootstrapRomData);
		}

		public void Startup()
		{
			LoadBios();

			cpu.Startup();
			cpu.SetStackPointer(0xFFFF);
			vdp.Startup();
			psg.Startup();
		}

		public void Reset()
		{
			cpu.Reset();
			vdp.Reset();
			psg.Reset();

			portControls1 = portControls2 = 0xFFFF;
			controlsReadMode = 0x00;

			isNmi = isNmiPending = false;

			OnEmulationReset(EventArgs.Empty);
		}

		public void Shutdown()
		{
			cpu?.Shutdown();
			vdp?.Shutdown();
			psg?.Shutdown();
		}

		public void SetState(Dictionary<string, dynamic> state)
		{
			SaveStateHandler.PerformSetState(cartridge, state[nameof(cartridge)]);
			wram = state[nameof(wram)];
			SaveStateHandler.PerformSetState(cpu, state[nameof(cpu)]);
			SaveStateHandler.PerformSetState(vdp, state[nameof(vdp)]);
			SaveStateHandler.PerformSetState(psg, state[nameof(psg)]);

			portControls1 = state[nameof(portControls1)];
			portControls2 = state[nameof(portControls2)];
			controlsReadMode = state[nameof(controlsReadMode)];
			isNmi = state[nameof(isNmi)];
			isNmiPending = state[nameof(isNmiPending)];

			ReconfigureSystem();
		}

		public Dictionary<string, dynamic> GetState()
		{
			return new Dictionary<string, dynamic>
			{
				[nameof(cartridge)] = SaveStateHandler.PerformGetState(cartridge),
				[nameof(wram)] = wram,
				[nameof(cpu)] = SaveStateHandler.PerformGetState(cpu),
				[nameof(vdp)] = SaveStateHandler.PerformGetState(vdp),
				[nameof(psg)] = SaveStateHandler.PerformGetState(psg),

				[nameof(portControls1)] = portControls1,
				[nameof(portControls2)] = portControls2,
				[nameof(controlsReadMode)] = controlsReadMode,
				[nameof(isNmi)] = isNmi,
				[nameof(isNmiPending)] = isNmiPending
			};
		}

		public Dictionary<string, dynamic> GetDebugInformation()
		{
			var dict = new Dictionary<string, dynamic>
			{
				{ "CyclesInFrame", currentMasterClockCyclesInFrame },
			};

			return dict;
		}

		public void Load(byte[] romData, byte[] ramData, Type mapperType)
		{
			if (mapperType == null)
				mapperType = typeof(ColecoCartridge);

			cartridge = (ICartridge)Activator.CreateInstance(mapperType, new object[] { romData.Length, 0 });
			cartridge.LoadRom(romData);
		}

		public byte[] GetCartridgeRam()
		{
			return cartridge?.GetRamData();
		}

		public bool IsCartridgeRamSaveNeeded()
		{
			if (cartridge == null) return false;
			return cartridge.IsRamSaveNeeded();
		}

		public virtual void RunFrame()
		{
			while (currentMasterClockCyclesInFrame < totalMasterClockCyclesInFrame)
				RunStep();

			currentMasterClockCyclesInFrame -= totalMasterClockCyclesInFrame;
		}

		public void RunStep()
		{
			double currentCpuClockCycles = 0.0;
			currentCpuClockCycles += cpu.Step();

			double currentMasterClockCycles = (currentCpuClockCycles * 3.0);

			vdp.Step((int)Math.Round(currentMasterClockCycles));

			if (vdp.InterruptLine == InterruptState.Assert && !isNmi) isNmiPending = true;
			isNmi = (vdp.InterruptLine == InterruptState.Assert);
			if (isNmiPending)
			{
				isNmiPending = false;
				cpu.SetInterruptLine(InterruptType.NonMaskable, InterruptState.Assert);
			}

			psg.Step((int)Math.Round(currentCpuClockCycles));

			currentMasterClockCyclesInFrame += (int)Math.Round(currentMasterClockCycles);
		}

		private void ParseInput(PollInputEventArgs eventArgs)
		{
			var keysDown = eventArgs.Keyboard;

			ushort dataCtrl1 = 0x0000;

			if (keysDown.Contains(configuration.ControlsKeypad1)) dataCtrl1 = (ushort)KeyJoyButtons.KeyNumber1;
			if (keysDown.Contains(configuration.ControlsKeypad2)) dataCtrl1 = (ushort)KeyJoyButtons.KeyNumber2;
			if (keysDown.Contains(configuration.ControlsKeypad3)) dataCtrl1 = (ushort)KeyJoyButtons.KeyNumber3;
			if (keysDown.Contains(configuration.ControlsKeypad4)) dataCtrl1 = (ushort)KeyJoyButtons.KeyNumber4;
			if (keysDown.Contains(configuration.ControlsKeypad5)) dataCtrl1 = (ushort)KeyJoyButtons.KeyNumber5;
			if (keysDown.Contains(configuration.ControlsKeypad6)) dataCtrl1 = (ushort)KeyJoyButtons.KeyNumber6;
			if (keysDown.Contains(configuration.ControlsKeypad7)) dataCtrl1 = (ushort)KeyJoyButtons.KeyNumber7;
			if (keysDown.Contains(configuration.ControlsKeypad8)) dataCtrl1 = (ushort)KeyJoyButtons.KeyNumber8;
			if (keysDown.Contains(configuration.ControlsKeypad9)) dataCtrl1 = (ushort)KeyJoyButtons.KeyNumber9;
			if (keysDown.Contains(configuration.ControlsKeypad0)) dataCtrl1 = (ushort)KeyJoyButtons.KeyNumber0;
			if (keysDown.Contains(configuration.ControlsKeypadStar)) dataCtrl1 = (ushort)KeyJoyButtons.KeyStarKey;
			if (keysDown.Contains(configuration.ControlsKeypadNumberSign)) dataCtrl1 = (ushort)KeyJoyButtons.KeyNumberSignKey;

			if (keysDown.Contains(configuration.ControlsButtonRight)) dataCtrl1 |= (ushort)KeyJoyButtons.JoyRightButton;

			if (keysDown.Contains(configuration.ControlsUp)) dataCtrl1 |= (ushort)KeyJoyButtons.JoyUp;
			if (keysDown.Contains(configuration.ControlsDown)) dataCtrl1 |= (ushort)KeyJoyButtons.JoyDown;
			if (keysDown.Contains(configuration.ControlsLeft)) dataCtrl1 |= (ushort)KeyJoyButtons.JoyLeft;
			if (keysDown.Contains(configuration.ControlsRight)) dataCtrl1 |= (ushort)KeyJoyButtons.JoyRight;
			if (keysDown.Contains(configuration.ControlsButtonLeft)) dataCtrl1 |= (ushort)KeyJoyButtons.JoyLeftButton;

			portControls1 = (ushort)~dataCtrl1;

			// TODO: controller 2

			portControls2 = 0xFFFF;
		}

		private byte ReadMemory(ushort address)
		{
			if (address >= 0x0000 && address <= 0x1FFF)
			{
				return (bios?.Read(address) ?? 0x00);
			}
			else if (address >= 0x2000 && address <= 0x3FFF)
			{
				/* Expansion port */
			}
			else if (address >= 0x4000 && address <= 0x5FFF)
			{
				/* Expansion port */
			}
			else if (address >= 0x6000 && address <= 0x7FFF)
			{
				return wram[address & (ramSize - 1)];
			}
			else if (address >= 0x8000 && address <= 0xFFFF)
			{
				return (cartridge != null ? cartridge.Read(address) : (byte)0x00);
			}

			/* Cannot read from address, return 0 */
			return 0x00;
		}

		private void WriteMemory(ushort address, byte value)
		{
			if (address >= 0x0000 && address <= 0x1FFF)
			{
				/* Can't write to BIOS */
			}
			else if (address >= 0x2000 && address <= 0x3FFF)
			{
				/* Expansion port */
			}
			else if (address >= 0x4000 && address <= 0x5FFF)
			{
				/* Expansion port */
			}
			else if (address >= 0x6000 && address <= 0x7FFF)
			{
				wram[address & (ramSize - 1)] = value;
			}
			else if (address >= 0x8000 && address <= 0xFFFF)
			{
				cartridge?.Write(address, value);
			}
		}

		private byte ReadPort(byte port)
		{
			switch (port & 0xE0)
			{
				case 0xA0:                          /* VDP ports */
					return vdp.ReadPort(port);

				case 0xE0:                          /* Controls */
					if ((port & 0x01) == 0)
						return (controlsReadMode == 0x00 ? (byte)(portControls1 & 0xFF) : (byte)((portControls1 >> 8) & 0xFF));
					else
						return (controlsReadMode == 0x00 ? (byte)(portControls2 & 0xFF) : (byte)((portControls2 >> 8) & 0xFF));

				default:
					return 0xFF;                    /* Not connected */
			}
		}

		public void WritePort(byte port, byte value)
		{
			switch (port & 0xE0)
			{
				case 0x80:                          /* Control mode (keypad/right buttons) */
					controlsReadMode = 0x00;
					break;

				case 0xA0:                          /* VDP */
					vdp.WritePort(port, value);
					break;

				case 0xC0:                          /* Control mode (joystick/left buttons) */
					controlsReadMode = 0x01;
					break;

				case 0xE0:                          /* PSG */
					psg.WritePort(port, value);
					break;
			}
		}
	}
}
