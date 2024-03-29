﻿using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Construction;
using Mono.Cecil;

namespace Papier
{
    public class ProjectBuilder
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        public string ProjectName;
        public string ProjectFolder;
        public bool DebugBuild;
        public Dictionary<string, string> References;
        private bool _built = false;

        public ProjectBuilder()
        {
            References = new Dictionary<string, string>();
        }

        public ProjectBuilder WithReference(string referenceName, string referencePath)
        {
            References.Add(referenceName, referencePath);
            return this;
        }

        public ProjectBuilder WithReference(string referencePath)
        {
            return WithReference(Path.GetFileNameWithoutExtension(referencePath), referencePath);
        }

        public void Build()
        {
            if (_built)
            {
                throw new InvalidOperationException("Has already been built");
            }
            
            Logger.Info($"Building {ProjectName} into {ProjectFolder}");
            var root = ProjectRootElement.Create();
            root.DefaultTargets = "Build";
            var group = root.AddPropertyGroup();
            group.AddProperty("Configuration", DebugBuild ? "Debug" : "Release");
            group.AddProperty("Platform", "x64");
            
            group.AddProperty("OutputType", "Library");
            group.AddProperty("OutputPath", "bin\\");
            group.AddProperty("AssemblyName", ProjectName);
            group.AddProperty("TargetFrameworkVersion", "v4.7.1"); // ??
            group.AddProperty("LangVersion", "8"); // ??
                
            var compileSet = root.AddItemGroup();
            // TODO: Could also support specifying a sourceset
            compileSet.AddItem("Compile", "*.cs");

            var referenceItemGroup = root.AddItemGroup();
            foreach (var (key, value) in References)
            {
                referenceItemGroup.AddItem("Reference", key).AddMetadata("HintPath", value);
            }

            root.AddImport(@"$(MSBuildToolsPath)\Microsoft.CSharp.targets");
            root.Save(Path.Combine(ProjectFolder, $"{ProjectName}.csproj"));
            _built = true;
        }

        /*group.AddProperty("DefineConstants",
                    "UNITY_2020_3_12;UNITY_2020_3;UNITY_2020;UNITY_5_3_OR_NEWER;UNITY_5_4_OR_NEWER;UNITY_5_5_OR_NEWER;
                    UNITY_5_6_OR_NEWER;UNITY_2017_1_OR_NEWER;UNITY_2017_2_OR_NEWER;UNITY_2017_3_OR_NEWER;UNITY_2017_4_OR_NEWER;
                    UNITY_2018_1_OR_NEWER;UNITY_2018_2_OR_NEWER;UNITY_2018_3_OR_NEWER;UNITY_2018_4_OR_NEWER;UNITY_2019_1_OR_NEWER;
                    UNITY_2019_2_OR_NEWER;UNITY_2019_3_OR_NEWER;UNITY_2019_4_OR_NEWER;UNITY_2020_1_OR_NEWER;UNITY_2020_2_OR_NEWER;
                    UNITY_2020_3_OR_NEWER;PLATFORM_ARCH_64;UNITY_64;UNITY_INCLUDE_TESTS;USE_SEARCH_ENGINE_API;SCENE_TEMPLATE_MODULE;
                    ENABLE_AR;ENABLE_AUDIO;ENABLE_CACHING;ENABLE_CLOTH;ENABLE_EVENT_QUEUE;ENABLE_MICROPHONE;ENABLE_MULTIPLE_DISPLAYS;
                    ENABLE_PHYSICS;ENABLE_TEXTURE_STREAMING;ENABLE_VIRTUALTEXTURING;ENABLE_UNET;ENABLE_LZMA;ENABLE_UNITYEVENTS;ENABLE_VR;
                    ENABLE_WEBCAM;ENABLE_UNITYWEBREQUEST;ENABLE_WWW;ENABLE_CLOUD_SERVICES;ENABLE_CLOUD_SERVICES_COLLAB;
                    ENABLE_CLOUD_SERVICES_COLLAB_SOFTLOCKS;ENABLE_CLOUD_SERVICES_ADS;ENABLE_CLOUD_SERVICES_USE_WEBREQUEST;
                    ENABLE_CLOUD_SERVICES_CRASH_REPORTING;ENABLE_CLOUD_SERVICES_PURCHASING;ENABLE_CLOUD_SERVICES_ANALYTICS;
                    ENABLE_CLOUD_SERVICES_UNET;ENABLE_CLOUD_SERVICES_BUILD;ENABLE_CLOUD_LICENSE;ENABLE_EDITOR_HUB_LICENSE;
                    ENABLE_WEBSOCKET_CLIENT;ENABLE_DIRECTOR_AUDIO;ENABLE_DIRECTOR_TEXTURE;ENABLE_MANAGED_JOBS;
                    ENABLE_MANAGED_TRANSFORM_JOBS;ENABLE_MANAGED_ANIMATION_JOBS;ENABLE_MANAGED_AUDIO_JOBS;INCLUDE_DYNAMIC_GI;
                    ENABLE_MONO_BDWGC;ENABLE_SCRIPTING_GC_WBARRIERS;PLATFORM_SUPPORTS_MONO;RENDER_SOFTWARE_CURSOR;ENABLE_VIDEO;
                    PLATFORM_STANDALONE;PLATFORM_STANDALONE_WIN;UNITY_STANDALONE_WIN;UNITY_STANDALONE;ENABLE_RUNTIME_GI;
                    ENABLE_MOVIES;ENABLE_NETWORK;ENABLE_CRUNCH_TEXTURE_COMPRESSION;ENABLE_OUT_OF_PROCESS_CRASH_HANDLER;
                    ENABLE_CLUSTER_SYNC;ENABLE_CLUSTERINPUT;PLATFORM_UPDATES_TIME_OUTSIDE_OF_PLAYER_LOOP;
                    GFXDEVICE_WAITFOREVENT_MESSAGEPUMP;ENABLE_WEBSOCKET_HOST;ENABLE_MONO;NET_4_6;ENABLE_PROFILER;DEBUG;
                    TRACE;UNITY_ASSERTIONS;UNITY_EDITOR;UNITY_EDITOR_64;UNITY_EDITOR_WIN;ENABLE_UNITY_COLLECTIONS_CHECKS;
                    ENABLE_BURST_AOT;UNITY_TEAM_LICENSE;ENABLE_CUSTOM_RENDER_TEXTURE;ENABLE_DIRECTOR;ENABLE_LOCALIZATION;
                    ENABLE_SPRITES;ENABLE_TERRAIN;ENABLE_TILEMAP;ENABLE_TIMELINE;ENABLE_LEGACY_INPUT_MANAGER;
                    CSHARP_7_OR_LATER;CSHARP_7_3_OR_NEWER");*/
    }
}