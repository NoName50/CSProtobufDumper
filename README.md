# CSProtobufDumper
A tool to extract .proto definitions from compiled C# assemblies that use Google.Protobuf

Target Game: Black Myth: Wukong (Technically, any unobfuscated .net managed program that uses Google.Protobuf is dumpable)

# KNOWN ISSUES

1. OneOf is not supported

2. Field numbers always start at 1, which may not match the original numbering

## USAGE

1. build it via Visual Studio 2022 or `dotnet build`

2. put all assemblies(.dll) in same folder as the executable

3. run the program

4. you will be left with an `Output` folder, containing the protobuf definitions

5. for more usage, run it with `-h`

## Credit
- [Dumpcs2Protobuf](https://github.com/Hiro420/Dumpcs2Protobuf): Code Reference

### Example proto class (method bodies are stripped):
```cs
public sealed class ActivationStateSyncWrapper : IMessage<ActivationStateSyncWrapper>, IMessage, IEquatable<ActivationStateSyncWrapper>, IDeepCloneable<ActivationStateSyncWrapper>
{
	private static readonly MessageParser<ActivationStateSyncWrapper> _parser;

	private UnknownFieldSet _unknownFields;

	private int syncFlag_;

	private int syncIdx_;

	private ActivationState value_;

	public static MessageParser<ActivationStateSyncWrapper> Parser;

	public int SyncFlag;

	public int SyncIdx;

	public ActivationState Value;

	public ActivationStateSyncWrapper(ActivationStateSyncWrapper other);

	public ActivationStateSyncWrapper Clone();

	public override bool Equals(object other);

	public bool Equals(ActivationStateSyncWrapper other);

	public override int GetHashCode();

	public void WriteTo(CodedOutputStream output);

	public int CalculateSize();

	public void MergeFrom(ActivationStateSyncWrapper other);

	public void MergeFrom(CodedInputStream input);
}
```

### Example of enum class:
```cs
public enum ActivationState
{
	NeverActivated,
	Active,
	WasActive
}
```

### Example of dumped proto:
```protobuf
syntax = "proto3";

package ArchiveB1;

import "ArchiveB1.ActivationState.proto";

option csharp_namespace = "ArchiveB1";

message ActivationStateSyncWrapper {
  int32 sync_flag = 1;
  int32 sync_idx = 2;
  ArchiveB1.ActivationState value = 3;
}
```