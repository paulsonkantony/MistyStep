using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using MistyStep.Core.Network;

namespace MistyStep.Windows.Network;

/// <summary>
/// Reads each adapter's Windows network category (Public, Private, Domain) through INetworkListManager:
/// enumerate the network connections, match each one's adapter id, and ask its network for the category.
/// Any failure, or an adapter with no connection, reports Public, so Home Wi-Fi mode fails closed.
/// </summary>
internal static partial class NetworkCategoryReader
{
    private static readonly Guid NetworkListManagerClsid = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");
    private static readonly StrategyBasedComWrappers ComWrappers = new();

    /// <summary>Returns a lookup from adapter id (as in <c>NetworkInterface.Id</c>) to category.</summary>
    public static Func<string, NetworkCategory> Open()
    {
        var categories = ReadAll();
        return adapterId => Guid.TryParse(adapterId, out var guid) && categories.TryGetValue(guid, out var category)
            ? category
            : NetworkCategory.Public;
    }

    internal static Dictionary<Guid, NetworkCategory> ReadAll()
    {
        var result = new Dictionary<Guid, NetworkCategory>();
        try
        {
            var iid = typeof(INetworkListManager).GUID;
            if (CoCreateInstance(NetworkListManagerClsid, IntPtr.Zero, ClsctxInprocServer, iid, out var pointer) < 0 || pointer == IntPtr.Zero)
                return result;

            INetworkListManager manager;
            try { manager = (INetworkListManager)ComWrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.None); }
            finally { Marshal.Release(pointer); }

            if (manager.GetNetworkConnections(out var connections) < 0 || connections is null) return result;

            // Bounded, in case an enumerator misbehaves.
            for (var i = 0; i < 256; i++)
            {
                if (connections.Next(1, out var connection, out var fetched) != 0 || fetched != 1 || connection is null) break;
                if (connection.GetAdapterId(out var adapterId) < 0) continue;
                if (connection.GetNetwork(out var network) < 0 || network is null) continue;
                if (network.GetCategory(out var category) < 0) continue;

                var mapped = category switch
                {
                    1 => NetworkCategory.Private,
                    2 => NetworkCategory.DomainAuthenticated,
                    _ => NetworkCategory.Public,
                };

                // An adapter on several networks counts as Public if any of them is.
                result[adapterId] = result.TryGetValue(adapterId, out var existing) && existing == NetworkCategory.Public
                    ? NetworkCategory.Public
                    : mapped;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // Fall through with what was read; missing adapters report Public.
        }

        return result;
    }

    private const uint ClsctxInprocServer = 0x1;

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, in Guid riid, out IntPtr ppv);

    // netlistmgr.idl. These are dual interfaces: four IDispatch slots come first in each vtable.
    // Only slots up to the last method called need declaring, in order.

    [GeneratedComInterface]
    [Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B")]
    internal partial interface INetworkListManager
    {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint index, uint lcid, out IntPtr typeInfo);
        [PreserveSig] int GetIDsOfNames(in Guid riid, IntPtr names, uint count, uint lcid, IntPtr dispIds);
        [PreserveSig] int Invoke(int dispId, in Guid riid, uint lcid, ushort flags, IntPtr parameters, IntPtr result, IntPtr exceptionInfo, IntPtr argError);
        [PreserveSig] int GetNetworks(int flags, out IntPtr enumNetworks);
        [PreserveSig] int GetNetwork(Guid networkId, out IntPtr network);
        [PreserveSig] int GetNetworkConnections(out IEnumNetworkConnections? enumConnections);
    }

    [GeneratedComInterface]
    [Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B")]
    internal partial interface IEnumNetworkConnections
    {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint index, uint lcid, out IntPtr typeInfo);
        [PreserveSig] int GetIDsOfNames(in Guid riid, IntPtr names, uint count, uint lcid, IntPtr dispIds);
        [PreserveSig] int Invoke(int dispId, in Guid riid, uint lcid, ushort flags, IntPtr parameters, IntPtr result, IntPtr exceptionInfo, IntPtr argError);
        [PreserveSig] int get_NewEnum(out IntPtr enumVariant);

        /// <summary>Called with <paramref name="count"/> = 1 only, so one interface pointer is returned.</summary>
        [PreserveSig] int Next(uint count, out INetworkConnection? connection, out uint fetched);
    }

    [GeneratedComInterface]
    [Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B")]
    internal partial interface INetworkConnection
    {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint index, uint lcid, out IntPtr typeInfo);
        [PreserveSig] int GetIDsOfNames(in Guid riid, IntPtr names, uint count, uint lcid, IntPtr dispIds);
        [PreserveSig] int Invoke(int dispId, in Guid riid, uint lcid, ushort flags, IntPtr parameters, IntPtr result, IntPtr exceptionInfo, IntPtr argError);
        [PreserveSig] int GetNetwork(out INetwork? network);
        [PreserveSig] int get_IsConnectedToInternet(out short connected);
        [PreserveSig] int get_IsConnected(out short connected);
        [PreserveSig] int GetConnectivity(out int connectivity);
        [PreserveSig] int GetConnectionId(out Guid connectionId);
        [PreserveSig] int GetAdapterId(out Guid adapterId);
    }

    [GeneratedComInterface]
    [Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B")]
    internal partial interface INetwork
    {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint index, uint lcid, out IntPtr typeInfo);
        [PreserveSig] int GetIDsOfNames(in Guid riid, IntPtr names, uint count, uint lcid, IntPtr dispIds);
        [PreserveSig] int Invoke(int dispId, in Guid riid, uint lcid, ushort flags, IntPtr parameters, IntPtr result, IntPtr exceptionInfo, IntPtr argError);
        [PreserveSig] int GetName(out IntPtr name);
        [PreserveSig] int SetName(IntPtr name);
        [PreserveSig] int GetDescription(out IntPtr description);
        [PreserveSig] int SetDescription(IntPtr description);
        [PreserveSig] int GetNetworkId(out Guid networkId);
        [PreserveSig] int GetDomainType(out int domainType);
        [PreserveSig] int GetNetworkConnections(out IntPtr enumConnections);
        [PreserveSig] int GetTimeCreatedAndConnected(out uint createdLow, out uint createdHigh, out uint connectedLow, out uint connectedHigh);
        [PreserveSig] int get_IsConnectedToInternet(out short connected);
        [PreserveSig] int get_IsConnected(out short connected);
        [PreserveSig] int GetConnectivity(out int connectivity);
        [PreserveSig] int GetCategory(out int category);
    }
}
