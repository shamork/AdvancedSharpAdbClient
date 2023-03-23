﻿// <copyright file="StartServerResult.cs" company="The Android Open Source Project, Ryan Conrad, Quamotion, yungd1plomat, wherewhere">
// Copyright (c) The Android Open Source Project, Ryan Conrad, Quamotion, yungd1plomat, wherewhere. All rights reserved.
// </copyright>

namespace AdvancedSharpAdbClient.WinRT
{
    /// <summary>
    /// Gives information about a <see cref="AdbServer.StartServer(string, bool)"/> operation.
    /// </summary>
    public enum StartServerResult
    {
        /// <summary>
        /// The adb server was already running. The server was not restarted.
        /// </summary>
        AlreadyRunning,

        /// <summary>
        /// The adb server was running, but was running an outdated version of adb.
        /// The server was stopped and a newer version of the server was started.
        /// </summary>
        RestartedOutdatedDaemon,

        /// <summary>
        /// The adb server was not running, and a new instance of the adb server was started.
        /// </summary>
        Started
    }
}
