namespace Promul.Common.Structs
{
 public enum RelayControlMessageType : byte
 {
  /*
   * Bidirectional. Relayed data.
   */
  Data = 0x00,
    
  /*
   * Relay -> Host: A client has connected to the relay.
   * Relay -> Client: You are connected to the relay.
   */
  Connected = 0x10,
    
  /*
   * Relay -> Host: A client has disconnected from the relay.
   */
  Disconnected = 0x12,
  
  /*
   * Host -> Relay: Requesting the relay disconnect a member of the relay.
   */
  KickFromRelay = 0x01
 }
}