﻿/*
 *
 * Copyright (c) 2022-2023 Carbon Community 
 * All rights reserved.
 *
 */

using ProtoBuf;

namespace Carbon.Client.Packets;

[ProtoContract(InferTagFromName = true)]
public class ClientInfo : BasePacket
{
	public int ScreenWidth { get; set; }
	public int ScreenHeight { get; set; }

	public override void Dispose()
	{

	}
}
