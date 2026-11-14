#!/bin/bash

OutputDir="$HOME/.local/share/mforge"

if [ ! -d "$OutputDir" ]; then
    mkdir -p "$OutputDir"
fi

curl -L "https://github.com/gtmonkeforge/cli/releases/latest/download/mforge-linux-x64" -o "$OutputDir/mforge"

chmod +x "$OutputDir/mforge"

EXPORT_LINE="export PATH=\"\$PATH:$OutputDir\""

if [[ "$SHELL" == *"zsh"* ]]; then
    PROFILE_FILE="$HOME/.zshrc"
elif [[ "$SHELL" == *"bash"* ]]; then
    PROFILE_FILE="$HOME/.bashrc"
else
    PROFILE_FILE="$HOME/.profile"
fi

if ! grep -qF "$NEW_PATH" "$PROFILE_FILE" 2>/dev/null; then
    echo "" >> "$PROFILE_FILE"
fi

# We're done
echo "'mforge' command is installed"
echo "Terminal must be restarted for changes to take effect"