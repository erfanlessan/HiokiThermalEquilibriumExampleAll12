using System.Globalization;
using System.IO.Ports;
using System.Text;

namespace HiokiThermalEquilibriumExample;

[Flags]
public enum ArduinoOutput : uint
{
    None = 0,
    DiodeFlag = 1u << 0,
    IgbtFlag = 1u << 1,
    DcPlusTop = 1u << 2,
    UTop = 1u << 3,
    VTop = 1u << 4,
    WTop = 1u << 5,
    UEmitterTop = 1u << 6,
    VEmitterTop = 1u << 7,
    WEmitterTop = 1u << 8,
    UEmitterBottom = 1u << 9,
    VEmitterBottom = 1u << 10,
    WEmitterBottom = 1u << 11,
    AssemblySwitch = 1u << 12,
    WBottom = 1u << 13,
    VBottom = 1u << 14,
    UBottom = 1u << 15,
    DcPlusBottom = 1u << 16
}

public enum HeaterDevice
{
    IgbtUUpper,
    IgbtVUpper,
    IgbtWUpper,
    IgbtULower,
    IgbtVLower,
    IgbtWLower,
    DiodeUUpper,
    DiodeVUpper,
    DiodeWUpper,
    DiodeULower,
    DiodeVLower,
    DiodeWLower
}

public enum MonitorRelaySelection
{
    None,
    Igbt,
    Diode
}

public enum HeaterPowerOffTiming
{
    AtTrigger,
    AfterPostEvent
}

public static class HeaterRouting
{
    public static ArduinoOutput Build(HeaterDevice device, MonitorRelaySelection monitorRelay)
    {
        ArduinoOutput route = device switch
        {
            HeaterDevice.IgbtUUpper => ArduinoOutput.DcPlusTop | ArduinoOutput.UBottom,
            HeaterDevice.IgbtVUpper => ArduinoOutput.DcPlusTop | ArduinoOutput.VBottom,
            HeaterDevice.IgbtWUpper => ArduinoOutput.DcPlusTop | ArduinoOutput.WBottom,
            HeaterDevice.IgbtULower => ArduinoOutput.UTop | ArduinoOutput.UEmitterBottom,
            HeaterDevice.IgbtVLower => ArduinoOutput.VTop | ArduinoOutput.VEmitterBottom,
            HeaterDevice.IgbtWLower => ArduinoOutput.WTop | ArduinoOutput.WEmitterBottom,
            HeaterDevice.DiodeUUpper => ArduinoOutput.DcPlusBottom | ArduinoOutput.UTop,
            HeaterDevice.DiodeVUpper => ArduinoOutput.DcPlusBottom | ArduinoOutput.VTop,
            HeaterDevice.DiodeWUpper => ArduinoOutput.DcPlusBottom | ArduinoOutput.WTop,
            HeaterDevice.DiodeULower => ArduinoOutput.UBottom | ArduinoOutput.UEmitterTop,
            HeaterDevice.DiodeVLower => ArduinoOutput.VBottom | ArduinoOutput.VEmitterTop,
            HeaterDevice.DiodeWLower => ArduinoOutput.WBottom | ArduinoOutput.WEmitterTop,
            _ => throw new ArgumentOutOfRangeException(nameof(device), device, null)
        };

        return route | (monitorRelay switch
        {
            MonitorRelaySelection.None => ArduinoOutput.None,
            MonitorRelaySelection.Igbt => ArduinoOutput.IgbtFlag,
            MonitorRelaySelection.Diode => ArduinoOutput.DiodeFlag,
            _ => throw new ArgumentOutOfRangeException(nameof(monitorRelay), monitorRelay, null)
        });
    }
}

/// <summary>
/// Controls the Arduino firmware that accepts newline-terminated decimal bit-mask
/// commands in the form S&lt;number&gt;. Heating is enabled only by adding the
/// AssemblySwitch bit to an already configured device route.
/// </summary>
public sealed class ArduinoHeaterController : IAsyncDisposable
{
    private const uint ValidOutputMask = (1u << 17) - 1;
    private readonly SerialPort _serialPort;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private ArduinoOutput _configuredRoute;
    private bool _disposed;

    public ArduinoHeaterController(string portName, int baudRate = 9600)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        if (baudRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baudRate));
        }

        _serialPort = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            Encoding = Encoding.ASCII,
            Handshake = Handshake.None,
            NewLine = "\n",
            ReadTimeout = 2000,
            WriteTimeout = 2000,
            DtrEnable = false,
            RtsEnable = false
        };
    }

    public bool IsOpen => _serialPort.IsOpen;

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_serialPort.IsOpen)
        {
            return;
        }

        _serialPort.Open();

        // Most Arduino USB interfaces reset the board when the port opens.
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        _serialPort.DiscardInBuffer();
        _serialPort.DiscardOutBuffer();

        await AllOffAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ConfigureDeviceAsync(
        HeaterDevice device,
        MonitorRelaySelection monitorRelay,
        CancellationToken cancellationToken = default)
    {
        ArduinoOutput route = HeaterRouting.Build(device, monitorRelay);
        if ((route & ArduinoOutput.AssemblySwitch) != 0)
        {
            throw new InvalidOperationException("A device route must not enable the assembly switch.");
        }

        _configuredRoute = route;
        await SendMaskAsync(route, cancellationToken).ConfigureAwait(false);
    }

    public Task SetHeatingEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        ArduinoOutput mask = enabled
            ? _configuredRoute | ArduinoOutput.AssemblySwitch
            : _configuredRoute & ~ArduinoOutput.AssemblySwitch;
        return SendMaskAsync(mask, cancellationToken);
    }

    public async Task AllOffAsync(CancellationToken cancellationToken = default)
    {
        _configuredRoute = ArduinoOutput.None;
        await SendMaskAsync(ArduinoOutput.None, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendMaskAsync(ArduinoOutput outputs, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_serialPort.IsOpen)
        {
            throw new InvalidOperationException("The Arduino serial port is not open.");
        }

        uint mask = (uint)outputs;
        if ((mask & ~ValidOutputMask) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputs), "Only Arduino output bits 0 to 16 are valid.");
        }

        string command = "S" + mask.ToString(CultureInfo.InvariantCulture);
        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _serialPort.WriteLine(command);
            string response = _serialPort.ReadLine().Trim();
            string expected = "Received: " + command;
            if (!string.Equals(response, expected, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Unexpected Arduino response. Expected '{expected}', received '{response}'.");
            }
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (_serialPort.IsOpen)
        {
            try
            {
                await AllOffAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Best effort during disposal; the main program reports shutdown failures.
            }

            _serialPort.Close();
        }

        _disposed = true;
        _serialPort.Dispose();
        _commandLock.Dispose();
    }
}
