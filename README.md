# MailZort

MailZort is a .NET 9 worker project designed to run in a Docker container, functioning as a scheduled job. It connects to a configured email folder and sorts incoming mail based on user-defined rules. I use it with Kubernetes cron jobs Manifest below,

## Notes
- Emails get cached in an SQLite database thats put in the `data` folder in execution directory
- Appsetting.json file in the project gets copied to `data` folder in execution directory

## Features

- Automatically sorts incoming emails based on a flexible configuration.
- Runs as a .NET 9 worker project in a Docker container.
- Ideal for handling email categorization and organization tasks.


## Configuration

MailZort requires a configuration file named appSettings.json. Here's an example of a configuration file:

``` json
{
    "EmailSettings": {
        //Email Server - Host
         "Server": "mymail.com",
        //Email Server - Port
        "Port": 993,
         //Email Server - UserName
        "UserName": "myuser",
        //Email Server - Password
        "Password": "apassword",
        //Email Server - Should we use SSL
        "UseSsl": true,
        // Determines whether MailZort should pull all emails or use the email cache
        "PullMails": true,
        // The folder where deleted messages will be moved to.
        "Trash": "Deleted Messages",
        "TestMode": false,
        // When we pull messages , how many should we pull: "all", "not-seen", "recent"
        "SearchMode": "recent"
    },
    "rules": [
        {
            "Name": "bills",
            // What folder to look in
            "Folder": "Inbox",
            // How old a message should be before moving it
             "DaysOld": 1,
            // Folder to move matches too
            "MoveTo": "Bills",
            // What areas should we look for a value
            // Options: "All," "Subject," "Body," "Sender," "Recipient," "SenderEmail"
            "LookIn": 3,
             // Determines the type of expression to match values.
            // Options: "Contains," "DoesNotContain," "Is," "IsNot," "StartsWith," "EndsWith," "MatchesRegex," "DoesNotMatchRegex"
            "ExpressionType": 0,
             // Values to match in the email content.
            "Values": [
                "AT&T",
                "Amazon",
                "Steam"
            }
        },
        // Add more rules here...
    ]
}



```

## Kubernetes

K3 Manifest
``` yaml

apiVersion: batch/v1
kind: CronJob
metadata:
  name: mailzort
spec:
  schedule: "*/5 * * * *" # Runs every 5 minutes
  concurrencyPolicy: Forbid
  failedJobsHistoryLimit: 1
  successfulJobsHistoryLimit: 5
  jobTemplate:
    spec:
      template:
        spec:
          containers:
            - name: mailzort
              image: timdoddcool/mailzort:1.0
              imagePullPolicy: IfNotPresent
              volumeMounts:
                - name: mailzort-volume
                  mountPath: /app/data
              env:
                - name: EmailSettings__Server
                  value: "myserver"
                - name: EmailSettings__Port
                  value: "993"
                - name: EmailSettings__UserName
                  value: myuser
                - name: EmailSettings__Password
                  valueFrom:
                    secretKeyRef:
                      name: mysecretref
                      key: password
                - name: EmailSettings__UseSsl
                  value: "true"
          restartPolicy: Never
          imagePullSecrets:
            - name: mysecretref
          volumes:
            - name: mailzort-volume
              nfs:
                server: 192.168.1.51
                path: /volume1/docker/mailzort
                readOnly: no
```

## notes
```bash 
docker build -t mail-zort -f Dockerfile.arm64 .
docker tag mail-zort timdoddcool/mailzort:v3-arm64
docker push timdoddcool/mailzort:v3-arm64
```
