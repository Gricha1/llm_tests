#!/bin/bash
set -e
cd /mnt/c/Grisha/unity_projects/forest_survival
git add -A
git commit -F .git_commit_msg.txt
rm -f .git_commit_msg.txt
