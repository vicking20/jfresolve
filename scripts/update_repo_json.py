import json
import argparse
from datetime import datetime
import sys

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", required=True, help="Repo owner/name e.g. user/repo")
    parser.add_argument("--version", required=True, help="Version string e.g. 1.0.0.6")
    parser.add_argument("--zip-url", required=True, help="URL to the release zip")
    parser.add_argument("--checksum", required=True, help="MD5 checksum of the zip")
    parser.add_argument("--target-abi", default="10.11.0.0", help="Target ABI")
    parser.add_argument("--file", default="repository.json", help="Path to repository.json")
    args = parser.parse_args()

    repo_owner = args.repo.split('/')[0]

    try:
        with open(args.file, 'r') as f:
            data = json.load(f)
    except FileNotFoundError:
        print(f"Error: {args.file} not found.")
        sys.exit(1)

    if not data:
        print("Error: repository.json is empty")
        sys.exit(1)

    # Assuming a single plugin entry in the array
    plugin = data[0]

    # Update global info to match the fork
    plugin["owner"] = repo_owner
    # Assuming the user keeps jfresolve.png in the root
    plugin["imageUrl"] = f"https://raw.githubusercontent.com/{args.repo}/main/jfresolve.png"

    new_version = {
        "version": args.version,
        "changelog": f"Automated release {args.version}",
        "sourceUrl": args.zip_url,
        "checksum": args.checksum,
        "targetAbi": args.target_abi,
        "timestamp": datetime.utcnow().strftime("%Y-%m-%dT%H:%M:%SZ")
    }

    # Prepend new version
    # Check if version exists and replace it, or just prepend
    versions = plugin.get("versions", [])
    # Filter out if version already exists (to update it)
    versions = [v for v in versions if v.get("version") != args.version]

    versions.insert(0, new_version)
    plugin["versions"] = versions

    with open(args.file, 'w') as f:
        json.dump(data, f, indent=2)
        f.write('\n')

    print(f"Updated {args.file} with version {args.version}")

if __name__ == "__main__":
    main()
