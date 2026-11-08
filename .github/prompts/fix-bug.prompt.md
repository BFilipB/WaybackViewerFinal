---
mode: agent
description: Diagnose and fix a bug with a regression test
---
Bug: ${input:bug:Describe the symptom}

1. Find the root cause (do not hide symptoms).
2. Write a failing regression test first.
3. Apply the minimal fix.
4. Build, run the test and related tests.
5. Explain root cause and fix in 3-5 lines.
