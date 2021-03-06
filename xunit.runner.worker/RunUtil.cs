﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using xunit.runner.data;
using Xunit;
using Xunit.Abstractions;

namespace xunit.runner.worker
{
    internal sealed class RunUtil
    {
        private sealed class TestRunVisitor : TestMessageVisitor<ITestAssemblyFinished>
        {
            private readonly ClientWriter _writer;

            public TestRunVisitor(ClientWriter writer)
            {
                _writer = writer;
            }

            private void Process(string displayName, TestState state, string output = "")
            {
                Console.WriteLine($"{state} - {displayName}");
                var result = new TestResultData(displayName, state, output);

                _writer.Write(TestDataKind.Value);
                _writer.Write(result);
            }

            protected override bool Visit(ITestFailed testFailed)
            {
                var displayName = testFailed.TestCase.DisplayName;
                var builder = new StringBuilder();
                builder.AppendLine($"{displayName} FAILED:");
                for (int i = 0; i < testFailed.ExceptionTypes.Length; i++)
                {
                    builder.AppendLine($"\tException type: '{testFailed.ExceptionTypes[i]}', number: '{i}', parent: '{testFailed.ExceptionParentIndices[i]}'");
                    builder.AppendLine($"\tException message:");
                    builder.AppendLine(testFailed.Messages[i]);
                    builder.AppendLine($"\tException stacktrace");
                    builder.AppendLine(testFailed.StackTraces[i]);
                }
                builder.AppendLine();

                Process(testFailed.TestCase.DisplayName, TestState.Failed, builder.ToString());

                return _writer.IsConnected;
            }

            protected override bool Visit(ITestPassed testPassed)
            {
                Process(testPassed.TestCase.DisplayName, TestState.Passed);
                return _writer.IsConnected;
            }

            protected override bool Visit(ITestSkipped testSkipped)
            {
                Process(testSkipped.TestCase.DisplayName, TestState.Skipped);
                return _writer.IsConnected;
            }
        }

        private sealed class TestCaseDiscoverer : TestMessageVisitor<IDiscoveryCompleteMessage>
        {
            private readonly HashSet<string> _testCaseDisplayNameSet;
            private readonly List<ITestCase> _testCaseList;

            internal TestCaseDiscoverer(HashSet<string> testCaseDisplayNameSet, List<ITestCase> testCaseList)
            {
                _testCaseDisplayNameSet = testCaseDisplayNameSet;
                _testCaseList = testCaseList;
            }

            protected override bool Visit(ITestCaseDiscoveryMessage testCaseDiscovered)
            {
                var testCase = testCaseDiscovered.TestCase;
                if (_testCaseDisplayNameSet.Contains(testCase.DisplayName))
                {
                    _testCaseList.Add(testCaseDiscovered.TestCase);
                }

                return true;
            }
        }

        /// <summary>
        /// Read out the set of test case display names to run.
        /// </summary>
        private static List<string> ReadTestCaseDisplayNames(Stream stream)
        {
            using (var reader = new ClientReader(stream))
            {
                var list = new List<string>();
                while (reader.ReadKind() == TestDataKind.Value)
                {
                    list.Add(reader.ReadString());
                }

                return list;
            }
        }

        private static List<ITestCase> GetTestCaseList(XunitFrontController xunit, Stream stream)
        {
            var testCaseDisplayNames = ReadTestCaseDisplayNames(stream);
            var testCaseDisplayNameSet = new HashSet<string>(testCaseDisplayNames, StringComparer.Ordinal);
            var testCaseList = new List<ITestCase>();

            using (var discoverer = new TestCaseDiscoverer(testCaseDisplayNameSet, testCaseList))
            {
                xunit.Find(includeSourceInformation: false, messageSink: discoverer, discoveryOptions: TestFrameworkOptions.ForDiscovery());
                discoverer.Finished.WaitOne();
            }

            return testCaseList;
        }

        internal static void RunAll(string assemblyPath, Stream stream)
        {
            using (AssemblyHelper.SubscribeResolve())
            using (var xunit = new XunitFrontController(
                AppDomainSupport.IfAvailable,
                assemblyFileName: assemblyPath,
                shadowCopy: false,
                diagnosticMessageSink: new MessageVisitor()))
            using (var writer = new ClientWriter(stream))
            using (var testRunVisitor = new TestRunVisitor(writer))
            {
                xunit.RunAll(testRunVisitor, TestFrameworkOptions.ForDiscovery(), TestFrameworkOptions.ForExecution());
                testRunVisitor.Finished.WaitOne();
                writer.Write(TestDataKind.EndOfData);
            }
        }

        internal static void RunSpecific(string assemblyPath, Stream stream)
        {
            using (AssemblyHelper.SubscribeResolve())
            using (var xunit = new XunitFrontController(
                AppDomainSupport.IfAvailable,
                assemblyFileName: assemblyPath,
                shadowCopy: false,
                diagnosticMessageSink: new MessageVisitor()))
            using (var writer = new ClientWriter(stream))
            using (var testRunVisitor = new TestRunVisitor(writer))
            {
                var testCaseList = GetTestCaseList(xunit, stream);
                xunit.RunTests(testCaseList, testRunVisitor, TestFrameworkOptions.ForExecution());
                testRunVisitor.Finished.WaitOne();
                writer.Write(TestDataKind.EndOfData);
            }
        }
    }
}
