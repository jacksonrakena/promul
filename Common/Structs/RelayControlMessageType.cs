namespace Promul.Common.Structs
{
 public enum RelayControlMessageType : byte
 {
  /*
   * Bidirectional. Relayed data.
   */
  Data = 0x00,
    
  /*
   * Relay -> Client Only. You are connected to the server.
   */
  Connected = 0x10,
    
  /*
   * Relay -> Server Only. A client has disconnected from the relay.
   */
  ClientDisconnected = 0x12,
    
  /*
   * Relay -> Server Only. A client has disconnected from the relay.
   */
  ClientConnected = 0x11
 }
}