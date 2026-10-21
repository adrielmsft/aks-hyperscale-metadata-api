import contextlib
import io
from pathlib import Path
import unittest
from unittest.mock import Mock, patch
import xml.etree.ElementTree as ET

import test_summary as report


class EvidenceSummaryTests(unittest.TestCase):
    def test_test_results_include_every_outcome_class_and_duration(self):
        root = ET.fromstring(f'''<TestRun xmlns="{report.NS['t']}">
          <TestDefinitions><UnitTest id="1"><TestMethod className="Example.Tests" /></UnitTest></TestDefinitions>
          <Results>
            <UnitTestResult testId="1" testName="Create returns created" outcome="Passed" duration="00:00:00.125" />
            <UnitTestResult testId="1" testName="Missing returns 404" outcome="Failed" duration="00:00:01">
              <Output><ErrorInfo><Message>Expected 404 | got &lt;500&gt;</Message></ErrorInfo></Output>
            </UnitTestResult>
            <UnitTestResult testId="1" testName="Collector" outcome="NotExecuted" />
          </Results></TestRun>''')
        with patch.object(report.ET, 'parse', return_value=ET.ElementTree(root)), patch.object(report, 'annotation') as annotate:
            summary = '\n'.join(report.test_results([Path('fixture.trx')], 'Real HTTP E2E (Kestrel process)'))
        for text in ('3 tests: 1 passed, 1 failed/other, 1 skipped', 'Example.Tests',
                     'Create returns created', '✅ Pass', '❌ Fail', '⏭ Skip', '0.125 s',
                     'Expected 404 &#124; got &lt;500&gt;'):
            self.assertIn(text, summary)
        annotate.assert_called_once()
        self.assertIn('Missing returns 404', annotate.call_args.args[0])

    def test_annotations_escape_workflow_command_characters(self):
        output = io.StringIO()
        with patch.dict(report.os.environ, {'GITHUB_ACTIONS': 'true'}), contextlib.redirect_stdout(output):
            report.annotation('failure%\r\n::warning::not another annotation')
        self.assertEqual('::error::failure%25%0D%0A::warning::not another annotation\n', output.getvalue())

    def test_http_groups_guids_queries_and_fixtures_but_keeps_method_and_status(self):
        first = Mock()
        first.read_text.return_value = 'GET /api/v1/applications/12345678-abcd-1234-abcd-123456789abc?page=1 -> 200 (1.2 ms)\nPOST /api/v1/applications -> 413 (0.3 ms)\n'
        second = Mock()
        second.read_text.return_value = 'GET /api/v1/applications/87654321-abcd-1234-abcd-123456789abc?page=2 -> 404 (1.0 ms)\nGET /health/ready -> ERROR (0.5 ms)\n'
        summary = '\n'.join(report.http_results([first, second]))
        self.assertIn('4 requests across 2 fixture traces', summary)
        self.assertIn('| GET /api/v1/applications/{id} | 200 × 1, 404 × 1 | 2 |', summary)
        self.assertIn('| POST /api/v1/applications | 413 × 1 | 1 |', summary)
        self.assertIn('ERROR × 1', summary)
        self.assertNotIn('?page', summary)

    def test_coverage_preserves_totals_and_lists_assembly_and_class(self):
        root = ET.fromstring('''<coverage line-rate="0.9" branch-rate="0.8"><packages>
          <package name="MetadataApi" line-rate="0.9" branch-rate="0.8"><classes>
            <class name="ApplicationService" line-rate="1" branch-rate="0.5" />
          </classes></package></packages></coverage>''')
        with patch.object(report.ET, 'parse', return_value=ET.ElementTree(root)):
            summary = '\n'.join(report.coverage_results([Path('coverage.xml')]))
        self.assertIn('| Lines | 90.00% |', summary)
        self.assertIn('| Branches | 80.00% |', summary)
        self.assertIn('| **MetadataApi** | 90.00% | 80.00% |', summary)
        self.assertIn('| ApplicationService | 100.00% | 50.00% |', summary)

    def test_missing_reports_are_not_silently_successful(self):
        for function, arguments in ((report.test_results, ([], 'Suite')),
                                    (report.http_results, ([],)),
                                    (report.coverage_results, ([],))):
            with self.subTest(function=function.__name__), self.assertRaises(ValueError):
                function(*arguments)

    def test_rejects_stale_coverage_and_malformed_or_empty_http_traces(self):
        with self.assertRaises(ValueError):
            report.coverage_results([Path('one.xml'), Path('two.xml')])
        for content in ('', 'not a trace'):
            path = Mock()
            path.read_text.return_value = content
            with self.subTest(content=content), self.assertRaises(ValueError):
                report.http_results([path])


if __name__ == '__main__':
    unittest.main()
