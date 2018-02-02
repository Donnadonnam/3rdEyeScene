﻿using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Net;
using System.Net.Sockets;
using Tes.IO;
using Tes.Net;
using Tes.Server;
using Tes.Shapes;

namespace Tes.CoreTests
{
  public class ShapeTestFramework
  {
    public void TestShape<S>(S reference) where S : Shape, new()
    {
      ServerInfoMessage info = ServerInfoMessage.Default;
      info.CoordinateFrame = CoordinateFrame.XYZ;

      ServerSettings serverSettings = ServerSettings.Default;
      serverSettings.Flags &= ~(ServerFlag.Collate | ServerFlag.Compress);
      serverSettings.PortRange = 1000;
      IServer server = new TcpServer(serverSettings);

      // std::cout << "Start on port " << serverSettings.listenPort << std::endl;
      Assert.IsTrue(server.ConnectionMonitor.Start(ConnectionMonitorMode.Asynchronous));
      // std::cout << "Server listening on port " << server.connectionMonitor()->port() << std::endl;;

      // Create client and connect.

      TcpClient client = new TcpClient("127.0.0.1", server.ConnectionMonitor.Port);

      // Wait for connection.
      if (server.ConnectionMonitor.WaitForConnections(5000u) > 0)
      {
        server.ConnectionMonitor.CommitConnections(null);
      }

      Assert.Greater(server.ConnectionCount, 0);
      Assert.IsTrue(client.Connected);

      // Send server messages from another thread. Otherwise large packets may block.
      Thread sendThread = new Thread(() =>
      {
        Assert.Greater(server.Create(reference), 0);
        Assert.GreaterOrEqual(server.UpdateTransfers(0), 0);
        Assert.Greater(server.UpdateFrame(0.0f), 0);

        // Send end message.
        ControlMessage ctrlMsg = new ControlMessage();
        Assert.Greater(ServerUtil.SendMessage(server, (ushort)RoutingID.Control, (ushort)ControlMessageID.End, ctrlMsg), 0);
        Assert.Greater(server.UpdateFrame(0.0f), 0);
      });
      sendThread.Start();

      // Process client messages.
      ValidateClient(client, reference, info);

      client.Close();

      sendThread.Join();
      server.Close();

      server.ConnectionMonitor.Stop();
      server.ConnectionMonitor.Join();
    }

    void ValidateClient<S>(TcpClient socket, S reference, ServerInfoMessage serverInfo, uint timeoutSec = 10u) where S : Shape, new()
    {
      Stopwatch timer = new Stopwatch();
      ServerInfoMessage readServerInfo = new ServerInfoMessage();
      Dictionary<ulong, Resource> resources = new Dictionary<ulong, Resource>();
      PacketBuffer packetBuffer = new PacketBuffer(64 * 1024);
      S shape = new S();
      bool endMsgReceived = false;
      bool serverInfoRead = false;
      bool shapeMsgRead = false;

      timer.Start();

      // Keep looping until we get a CIdEnd ControlMessage or timeoutSec elapses.
      while (!endMsgReceived && timer.ElapsedMilliseconds / 1000u < timeoutSec)
      {
        if (socket.Available <= 0)
        {
          Thread.Yield();
          continue;
        }

        // Data available. Read from the network stream into a buffer and attempt to
        // read a valid message.
        packetBuffer.Append(socket.GetStream(), socket.Available);

        PacketBuffer completedPacket;
        bool crcOk = true;
        while ((completedPacket = packetBuffer.PopPacket(out crcOk)) != null || !crcOk)
        {
          Assert.IsTrue(crcOk);
          if (crcOk)
          {
            if (packetBuffer.DroppedByteCount != 0)
            {
              Console.Error.WriteLine("Dropped {0} bad bytes", packetBuffer.DroppedByteCount);
              packetBuffer.DroppedByteCount = 0;
            }

            Assert.AreEqual(PacketHeader.PacketMarker, completedPacket.Header.Marker);
            Assert.AreEqual(PacketHeader.PacketVersionMajor, completedPacket.Header.VersionMajor);
            Assert.AreEqual(PacketHeader.PacketVersionMinor, completedPacket.Header.VersionMinor);

            NetworkReader packetReader = new NetworkReader(completedPacket.CreateReadStream(true));

            switch (completedPacket.Header.RoutingID)
            {
              case (int)RoutingID.ServerInfo:
                serverInfoRead = true;
                Assert.IsTrue(readServerInfo.Read(packetReader));

                // Validate server info.
                Assert.AreEqual(serverInfo.TimeUnit, readServerInfo.TimeUnit);
                Assert.AreEqual(serverInfo.DefaultFrameTime, readServerInfo.DefaultFrameTime);
                Assert.AreEqual(serverInfo.CoordinateFrame, readServerInfo.CoordinateFrame);

                unsafe
                {
                  for (int i = 0; i < ServerInfoMessage.ReservedBytes; ++i)
                  {
                    Assert.AreEqual(serverInfo.Reserved[i], readServerInfo.Reserved[i]);
                  }
                }
                break;

              case (int)RoutingID.Control:
                {
                  // Only interested in the CIdEnd message to mark the end of the stream.
                  ControlMessage msg = new ControlMessage();
                  Assert.IsTrue(msg.Read(packetReader));

                  if (completedPacket.Header.MessageID == (int)ControlMessageID.End)
                  {
                    endMsgReceived = true;
                  }
                  break;
                }

              case (int)RoutingID.Mesh:
                //HandleMeshMessage(packetReader, resources);
                break;

              default:
                if (completedPacket.Header.RoutingID == reference.RoutingID)
                {
                  shapeMsgRead = true;
                  HandleShapeMessage(completedPacket, packetReader, shape, reference);
                }
                break;
            }
          }
        }
        // else fail?
      }

      Assert.IsTrue(serverInfoRead);
      Assert.IsTrue(shapeMsgRead);
      Assert.IsTrue(endMsgReceived);

      // Validate the shape state.
      if (shapeMsgRead)
      {
        ValidateShape(shape, reference, resources);
      }
    }


    void HandleShapeMessage(PacketBuffer packet, NetworkReader reader, Shape shape, Shape reference)
    {
      // Shape message the shape.
      uint shapeId = 0;

      // Peek the shape ID.
      shapeId = packet.PeekUInt32(PacketHeader.Size);

      Assert.AreEqual(shapeId, reference.ID);

      switch (packet.Header.MessageID)
      {
        case (int)ObjectMessageID.Create:
          Assert.IsTrue(shape.ReadCreate(reader));
          break;

        case (int)ObjectMessageID.Update:
          Assert.IsTrue(shape.ReadUpdate(reader));
          break;

        case (int)ObjectMessageID.Data:
          Assert.IsTrue(shape.ReadData(reader));
          break;
      }
    }


    void ValidateShape(Shape shape, Shape reference, Dictionary<ulong, Resource> resources)
    {
      Assert.AreEqual(reference.RoutingID, shape.RoutingID);
      Assert.AreEqual(reference.IsComplex, shape.IsComplex);

      Assert.AreEqual(reference.ID, shape.ID);
      Assert.AreEqual(reference.Category, shape.Category);
      Assert.AreEqual(reference.Flags, shape.Flags);
      //Assert.AreEqual(reference.data().reserved, shape.data().reserved);
      //Assert.AreEqual(reference.ID, shape.ID);

      Assert.AreEqual(reference.Colour, shape.Colour);

      Assert.AreEqual(reference.X, shape.X);
      Assert.AreEqual(reference.Y, shape.Y);
      Assert.AreEqual(reference.Z, shape.Z);

      Assert.AreEqual(reference.Rotation, shape.Rotation);

      Assert.AreEqual(reference.ScaleX, shape.ScaleX);
      Assert.AreEqual(reference.ScaleY, shape.ScaleY);
      Assert.AreEqual(reference.ScaleZ, shape.ScaleZ);
    }

    void Validate(Text2D shape, Text2D reference, Dictionary<ulong, Resource> resources)
    {
      ValidateShape((Shape)shape, (Shape)reference, resources);
      Assert.AreEqual(reference.Text, shape.Text);
    }

    void Validate(Text3D shape, Text3D reference, Dictionary<ulong, Resource> resources)
    {
      ValidateShape((Shape)shape, (Shape)reference, resources);
      Assert.AreEqual(reference.Text, shape.Text);
    }


    void ValidateShape(MeshShape shape, MeshShape reference, Dictionary<ulong, Resource> resources)
    {
      ValidateShape((Shape)shape, (Shape)reference, resources);

      Assert.AreEqual(reference.DrawType, shape.DrawType);

      if (reference.Vertices != null)
      {
        Assert.IsNotNull(shape.Vertices);
        Assert.AreEqual(reference.Vertices.Length, shape.Vertices.Length);
      }

      if (reference.Normals != null)
      {
        Assert.IsNotNull(shape.Normals);
        Assert.AreEqual(reference.Normals.Length, shape.Normals.Length);
      }

      if (reference.Colours != null)
      {
        Assert.IsNotNull(shape.Colours);
        Assert.AreEqual(reference.Colours.Length, shape.Colours.Length);
      }

      if (reference.Indices != null)
      {
        Assert.IsNotNull(shape.Indices);
        Assert.AreEqual(reference.Indices.Length, shape.Indices.Length);
      }

      if (reference.Vertices != null)
      {
        for (int i = 0; i < reference.Vertices.Length; ++i)
        {
          Assert.AreEqual(reference.Vertices[i], shape.Vertices[i]);
        }
      }

      if (reference.Normals != null)
      {
        for (int i = 0; i < reference.Normals.Length; ++i)
        {
          Assert.AreEqual(reference.Normals[i], shape.Normals[i]);
        }
      }

      if (reference.Colours != null)
      {
        for (int i = 0; i < reference.Colours.Length; ++i)
        {
          Assert.AreEqual(reference.Colours[i], shape.Colours[i]);
        }
      }

      if (reference.Indices != null)
      {
        for (int i = 0; i < reference.Indices.Length; ++i)
        {
          Assert.AreEqual(reference.Indices[i], shape.Indices[i]);
        }
      }
    }
  }
}
