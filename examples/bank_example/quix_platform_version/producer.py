import time
import uuid
from random import choice, randint, random
from time import sleep

from dotenv import load_dotenv

from quixstreams import Application
from quixstreams.models.serializers import QuixTimeseriesSerializer

load_dotenv("./bank_example/quix_platform_version/quix_vars.env")


app = Application()
serializer = QuixTimeseriesSerializer()
topic = app.topic(name="qts__purchase_events", value_serializer=serializer)


retailers = [
    "Billy Bob's Shop",
    "Tasty Pete's Burgers",
    "Mal-Wart",
    "Bikey Bikes",
    "Board Game Grove",
    "Food Emporium",
]


i = 0
# app.get_producer() automatically creates any topics made via `app.topic`
with app.get_producer() as producer:
    while i < 10000:
        account = randint(0, 10)
        account_id = f"A{'0'*(10-len(str(account)))}{account}"
        value = {
            "account_id": account_id,
            "account_class": "Gold" if account >= 8 else "Silver",
            "transaction_amount": randint(-2500, -1),
            "transaction_source": choice(retailers),
            "Timestamp": time.time_ns(),
        }
        print(f"Producing value {value}")
        # with current functionality, we need to manually serialize our data
        serialized = topic.serialize(
            key=account_id,
            value=value,
            headers={**serializer.extra_headers, "uuid": str(uuid.uuid4())},
        )
        producer.produce(
            topic=topic.name,
            headers=serialized.headers,
            key=serialized.key,
            value=serialized.value,
        )
        i += 1
        sleep(random())
