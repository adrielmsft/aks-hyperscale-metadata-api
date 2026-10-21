"""Render VSTest TRX, Coverlet Cobertura, and real-HTTP traces without API permissions."""

import argparse
from collections import Counter, defaultdict
import html
import os
from pathlib import Path
import re
import xml.etree.ElementTree as ET


NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
GUID = re.compile(r"(?i)(?<![0-9a-f])[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}(?![0-9a-f])")
TRACE = re.compile(r"^(\S+) (\S+) -> (\d{3}|ERROR) \((\d+(?:\.\d+)?) ms\)$")


def markdown(value):
    return html.escape(str(value)).replace("|", "&#124;").replace("`", "&#96;").replace("\r", "").replace("\n", "<br>")


def annotation(message, level="error"):
    if os.environ.get("GITHUB_ACTIONS") == "true":
        escaped = message.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
        print(f"::{level}::{escaped}")


def duration(value):
    if not value:
        return "—"
    hours, minutes, seconds = value.split(":")
    total = int(hours) * 3600 + int(minutes) * 60 + float(seconds)
    return f"{total:.3f} s"


def test_results(paths, title):
    lines = [f"## {title}", ""]
    if not paths:
        raise ValueError(f"{title}: no TRX results were generated; this suite did not complete.")
    classes = defaultdict(list)
    outcomes = Counter()
    for path in paths:
        root = ET.parse(path).getroot()
        definitions = {
            test.get("id"): test.find("t:TestMethod", NS)
            for test in root.findall("t:TestDefinitions/t:UnitTest", NS)
        }
        results = root.findall("t:Results/t:UnitTestResult", NS)
        if not results:
            raise ValueError(f"{title}: {path.name} contains no test results.")
        for result in results:
            method = definitions.get(result.get("testId"))
            name = method.get("className", "Unknown class") if method is not None else "Unknown class"
            classes[name].append(result)
            outcome = result.get("outcome", "Unknown")
            outcomes[outcome] += 1
            if outcome not in ("Passed", "NotExecuted"):
                error = result.find("t:Output/t:ErrorInfo", NS)
                details = " ".join(error.itertext()) if error is not None else "No failure details supplied."
                annotation(f"{title} / {name} / {result.get('testName')}: {details}"[:8000])
        for info in root.findall("t:ResultSummary/t:RunInfos/t:RunInfo", NS):
            if info.get("outcome") == "Error":
                annotation(f"{title}: {' '.join(info.itertext())}"[:8000])
    total = sum(outcomes.values())
    failed = total - outcomes["Passed"] - outcomes["NotExecuted"]
    lines += [f"**{total} tests: {outcomes['Passed']} passed, {failed} failed/other, "
              f"{outcomes['NotExecuted']} skipped.**", ""]
    for name, results in sorted(classes.items()):
        lines += [f"### {markdown(name)}", "", "| Test behavior | Result | Duration |", "| --- | --- | ---: |"]
        for result in sorted(results, key=lambda item: item.get("testName", "")):
            outcome = result.get("outcome", "Unknown")
            label = {"Passed": "✅ Pass", "Failed": "❌ Fail", "NotExecuted": "⏭ Skip"}.get(outcome, f"❌ {outcome}")
            lines.append(f"| {markdown(result.get('testName', 'Unknown test'))} | {label} | {duration(result.get('duration'))} |")
        lines.append("")
        for result in results:
            details = result.find("t:Output/t:ErrorInfo", NS)
            if details is not None:
                lines += [f"<details><summary>{markdown(result.get('testName'))}: failure / skip details</summary>", "",
                          markdown(" ".join(details.itertext())), "", "</details>", ""]
    return lines


def coverage_results(paths):
    if not paths:
        raise ValueError("No Cobertura coverage report was generated.")
    # There is one covered test project/TFM. Refuse ambiguous stale output rather than report the wrong run.
    if len(paths) != 1:
        raise ValueError("Expected one Cobertura report; clean the results directory before running tests.")
    root = ET.parse(paths[0]).getroot()
    percent = lambda element, metric: f"{float(element.get(metric, '0')) * 100:.2f}%"
    lines = ["## Unit and integration coverage", "", "| Metric | Coverage |", "| --- | ---: |",
             f"| Lines | {percent(root, 'line-rate')} |", f"| Branches | {percent(root, 'branch-rate')} |", "",
             "### Coverage by assembly and class", "", "| Assembly / class | Lines | Branches |", "| --- | ---: | ---: |"]
    for package in root.findall("packages/package"):
        lines.append(f"| **{markdown(package.get('name'))}** | {percent(package, 'line-rate')} | {percent(package, 'branch-rate')} |")
        for cls in sorted(package.findall("classes/class"), key=lambda item: item.get("name", "")):
            lines.append(f"| {markdown(cls.get('name'))} | {percent(cls, 'line-rate')} | {percent(cls, 'branch-rate')} |")
    return lines + [""]


def http_results(paths):
    if not paths:
        raise ValueError("No real HTTP traces were generated; check E2E fixture startup logs.")
    endpoints = defaultdict(Counter)
    for path in paths:
        for line in path.read_text(encoding="utf-8-sig").splitlines():
            match = TRACE.fullmatch(line)
            if not match:
                raise ValueError(f"Malformed HTTP trace line in {path}.")
            method, target, status, _ = match.groups()
            endpoint = GUID.sub("{id}", target.split("?", 1)[0])
            endpoints[f"{method} {endpoint}"][status] += 1
    count = sum(sum(statuses.values()) for statuses in endpoints.values())
    if not count:
        raise ValueError("Real HTTP trace files are empty.")
    lines = ["## E2E HTTP coverage (observed real requests)", "",
             f"**{count} requests across {len(paths)} fixture traces.** "
             "Includes readiness probes, unauthenticated clients, low-limit fixtures, and restarts.", "",
             "GUIDs become `{id}`; query variants are grouped by path. "
             "Counts are observations, not a replacement for test assertions or a claim of exhaustive route coverage.", "",
             "| Endpoint template | Status × count | Total |", "| --- | --- | ---: |"]
    for endpoint, statuses in sorted(endpoints.items()):
        observed = ", ".join(f"{status} × {count}" for status, count in sorted(statuses.items()))
        lines.append(f"| {markdown(endpoint)} | {observed} | {sum(statuses.values())} |")
    return lines + [""]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts", type=Path, default=Path("artifacts"))
    args = parser.parse_args()
    artifacts = args.artifacts
    # Use the attachment directory, not VSTest's duplicate <run>/In/<machine> staging copy.
    sections = [
        (test_results, sorted((artifacts / "unit-integration").glob("**/*.trx")), "Unit & SQLite integration"),
        (test_results, sorted((artifacts / "e2e-results").glob("**/*.trx")), "Real HTTP E2E (Kestrel process)"),
        (http_results, sorted((artifacts / "e2e").glob("**/http-trace.log"))),
        (coverage_results, sorted((artifacts / "unit-integration").glob("*/coverage.cobertura.xml"))),
    ]
    lines, errors = ["# Test evidence", ""], []
    for function, *arguments in sections:
        try:
            lines += function(*arguments)
        except (ValueError, OSError, ET.ParseError) as error:
            errors.append(str(error))
            lines += [f"**❌ Report unavailable:** {markdown(error)}", ""]
            annotation(str(error))
    report = "\n".join(lines) + "\n"
    artifacts.mkdir(parents=True, exist_ok=True)
    output = artifacts / "test-summary.md"
    output.write_text(report, encoding="utf-8")
    if summary := os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(summary, "a", encoding="utf-8") as stream:
            stream.write(report)
    print(f"Test evidence written to {output}")
    return bool(errors)


if __name__ == "__main__":
    raise SystemExit(main())
