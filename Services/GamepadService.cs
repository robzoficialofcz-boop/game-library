using System;
using System.Runtime.InteropServices;

namespace GameLibrary.Services;

/// <summary>
/// Leitura de controles compatíveis com XInput (Xbox e controles genéricos em modo XInput).
/// </summary>
public sealed class GamepadService
{
    private const uint ErrorSuccess = 0;
    private const string XInput14 = "xinput1_4.dll";
    private const string XInput910 = "xinput9_1_0.dll";

    [StructLayout(LayoutKind.Sequential)]
    private struct State
    {
        public uint PacketNumber;
        public Gamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Gamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [DllImport(XInput14, EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState14(uint userIndex, out State state);

    [DllImport(XInput910, EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState910(uint userIndex, out State state);

    public bool TryGetState(out ushort buttons, out short leftX, out short leftY)
    {
        buttons = 0;
        leftX = 0;
        leftY = 0;

        State state;
        uint result;
        try
        {
            result = XInputGetState14(0, out state);
        }
        catch (DllNotFoundException)
        {
            try { result = XInputGetState910(0, out state); }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }
        catch (EntryPointNotFoundException)
        {
            try { result = XInputGetState910(0, out state); }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }

        if (result != ErrorSuccess) return false;
        buttons = state.Gamepad.Buttons;
        leftX = state.Gamepad.ThumbLX;
        leftY = state.Gamepad.ThumbLY;
        return true;
    }
}
