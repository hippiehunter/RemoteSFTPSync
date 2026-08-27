@echo off
pushd %~dp0

rem Keep submodules pinned to the commit recorded by this repo.
git submodule sync --recursive
git submodule update --init --recursive --checkout

rem Do not auto-pull submodule branches here; that makes the superproject dirty.

popd
