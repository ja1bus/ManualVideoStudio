// 実 ffmpeg のプロセス数を数える試験・実画面の録画があるため、統合試験はクラスをまたいで並列実行しない。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
