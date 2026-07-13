# Changelog

All notable changes to this project are documented here. The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- Refactored ChainAnalysisView chart from dual-axis with manual zoom/threshold controls to three independent lanes (CE ν, NIFTY spot, PE ν), each with its own scale. Replaced manual alert thresholds with automatic opening-range band breach detection that triggers sound alerts. Extracted pure range-computation logic into testable library module. Added vitest testing and SonarJS/SonarAnalyzer code quality tooling.
